// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Runs bulk (and auto-sync) subtitle sync jobs one item at a time in the background,
/// so the dashboard doesn't have to wait around for a whole library to finish.
/// Bulk and auto-sync jobs always run standard mode with the default engine against every
/// external subtitle an item has - there's no UI in the background path to pick
/// engine/mode/penalty/subtitle.
/// </summary>
public class SyncQueueManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly ILogger<SyncQueueManager> _logger;
    private readonly object _lock = new();
    private readonly List<QueueItem> _items = new();
    private readonly Queue<Guid> _pending = new();
    private readonly HashSet<Guid> _queuedIds = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueueManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up items.</param>
    /// <param name="libraryService">Works out which items are eligible.</param>
    /// <param name="subtitleLocator">Finds external subtitles for an item.</param>
    /// <param name="registry">Used to pick the configured default engine.</param>
    /// <param name="runner">Runs the engine.</param>
    /// <param name="logger">Logger.</param>
    public SyncQueueManager(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SubtitleLocator subtitleLocator,
        EngineRegistry registry,
        EngineRunner runner,
        ILogger<SyncQueueManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _subtitleLocator = subtitleLocator;
        _registry = registry;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether anything is queued or running right now.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count > 0 || _items.Any(i => i.Status == QueueItemStatus.Running);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an item or folder id is skipped (either directly,
    /// or because one of its ancestor folders is skipped).
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item should be left alone.</returns>
    public static bool IsSkipped(BaseItem item)
    {
        var skipped = Plugin.Instance?.Configuration.SkippedItemIds;
        if (skipped is null || skipped.Count == 0)
        {
            return false;
        }

        for (var current = item; current is not null; current = current.GetParent())
        {
            if (skipped.Contains(current.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a progress snapshot for the dashboard to poll.
    /// </summary>
    /// <returns>Current queue state.</returns>
    public QueueSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var current = _items.FirstOrDefault(i => i.Status == QueueItemStatus.Running);
            return new QueueSnapshot
            {
                Running = current is not null || _pending.Count > 0,
                Total = _items.Count,
                Completed = _items.Count(i => i.Status is QueueItemStatus.Done or QueueItemStatus.Failed),
                CurrentItemName = current?.Name,
                Items = new List<QueueItem>(_items)
            };
        }
    }

    /// <summary>
    /// Starts a bulk sync job across every enabled library. Does nothing (and returns
    /// false) if a job is already running, since we only ever run one at a time.
    /// </summary>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueLibrary()
    {
        return StartBulkJob(_libraryService.GetItems());
    }

    /// <summary>
    /// Starts a bulk sync job for every syncable item under one library or folder.
    /// </summary>
    /// <param name="folderId">The library or folder to sync.</param>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueFolder(Guid folderId)
    {
        return StartBulkJob(_libraryService.GetItems(folderId));
    }

    /// <summary>
    /// Adds one item to whatever's already queued, starting the worker if it isn't
    /// running. Used by auto-sync when something new shows up, and by the scheduled task.
    /// Skipped items are silently ignored.
    /// </summary>
    /// <param name="item">The item to sync.</param>
    public void EnqueueItem(BaseItem item)
    {
        if (IsSkipped(item))
        {
            return;
        }

        lock (_lock)
        {
            if (!_queuedIds.Add(item.Id))
            {
                return;
            }

            _pending.Enqueue(item.Id);
            _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
        }

        EnsureWorkerRunning();
    }

    /// <summary>
    /// Queues a batch of items and waits for all of them to finish, reporting progress as
    /// it goes. This is what the scheduled task uses, since Jellyfin's task runner wants
    /// a task that stays alive for as long as the work does.
    /// </summary>
    /// <param name="items">The items to sync.</param>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were synced without an error.</returns>
    public async Task<int> RunBatchAsync(
        IReadOnlyList<BaseItem> items,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var succeeded = 0;

        lock (_lock)
        {
            // a scheduled run is a fresh job, so don't carry the last one's rows along -
            // the dashboard's progress strip reads this list and a whole library's worth
            // of finished items would otherwise pile up until the server restarts
            if (_pending.Count == 0 && !_items.Any(i => i.Status == QueueItemStatus.Running))
            {
                _items.Clear();
                _queuedIds.Clear();
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            lock (_lock)
            {
                if (_items.All(existing => existing.ItemId != item.Id))
                {
                    _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
                }
            }

            if (await ProcessOneAsync(item.Id, cancellationToken).ConfigureAwait(false))
            {
                succeeded++;
            }

            progress?.Report((i + 1) * 100.0 / items.Count);
        }

        return succeeded;
    }

    /// <summary>
    /// Builds a name for the queue list that says enough to tell items apart. Episodes get
    /// their series and episode number, since "Episode 3" on its own is useless.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>A display name.</returns>
    public static string DescribeItem(BaseItem item)
    {
        var name = item.Name ?? "Unknown";

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var series = episode.SeriesName;
            var numbers = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? $"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} "
                : string.Empty;

            return string.IsNullOrEmpty(series) ? numbers + name : $"{series} - {numbers}{name}";
        }

        return name;
    }

    private bool StartBulkJob(IReadOnlyList<BaseItem> items)
    {
        lock (_lock)
        {
            if (_pending.Count > 0 || _items.Any(i => i.Status == QueueItemStatus.Running))
            {
                return false;
            }

            _items.Clear();
            _queuedIds.Clear();

            foreach (var item in items)
            {
                _queuedIds.Add(item.Id);
                _pending.Enqueue(item.Id);
                _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
            }
        }

        if (items.Count == 0)
        {
            return false;
        }

        EnsureWorkerRunning();
        return true;
    }

    private void EnsureWorkerRunning()
    {
        lock (_lock)
        {
            if (_worker is not null && !_worker.IsCompleted)
            {
                return;
            }

            _worker = Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            Guid itemId;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                itemId = _pending.Dequeue();
                _queuedIds.Remove(itemId);
            }

            await ProcessOneAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> ProcessOneAsync(Guid itemId, CancellationToken cancellationToken)
    {
        SetItemStatus(itemId, QueueItemStatus.Running);

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "Item not found or has no video file");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return false;
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        if (subtitles.Count == 0)
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "No external subtitle found");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return false;
        }

        string? lastError = null;
        SyncResult? lastResult = null;

        var engine = _registry.GetDefault();
        var penalty = EngineRunner.ResolvePenalty(engine, null);

        foreach (var subtitle in subtitles)
        {
            var result = await _runner
                .RunAsync(engine, item.Path, subtitle.Path, SyncMode.Standard, penalty, outputMode: null, destinationOverride: null, cancellationToken)
                .ConfigureAwait(false);
            lastResult = result;

            if (!result.Success)
            {
                lastError = result.Error;
                _logger.LogWarning("Sync failed for {Item} ({Subtitle}): {Error}", item.Name, subtitle.Path, result.Error);
            }
        }

        var status = lastError is null ? MovieSyncStatus.Synced : MovieSyncStatus.Failed;
        SaveRecord(itemId, status, lastError, lastResult);
        SetItemStatus(itemId, status == MovieSyncStatus.Synced ? QueueItemStatus.Done : QueueItemStatus.Failed);
        return status == MovieSyncStatus.Synced;
    }

    private void SetItemStatus(Guid itemId, QueueItemStatus status)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is not null)
            {
                item.Status = status;
            }
        }
    }

    /// <summary>
    /// Saves (or updates) an item's sync record and persists the plugin config to disk.
    /// Shared by the background queue and the controller's synchronous single-item sync.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="status">The new status.</param>
    /// <param name="error">Error message, if the sync failed.</param>
    /// <param name="result">The engine result, if there is one.</param>
    public static void SaveRecord(Guid itemId, MovieSyncStatus status, string? error, SyncResult? result = null)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var records = plugin.Configuration.MovieRecords;
        var record = records.FirstOrDefault(r => r.ItemId == itemId);
        if (record is null)
        {
            record = new MovieSyncRecord { ItemId = itemId };
            records.Add(record);
        }

        record.Status = status;
        record.LastSyncUtc = DateTime.UtcNow;
        record.LastError = error;
        record.Mode = result?.Mode;
        record.Penalty = result?.Penalty;
        record.OffsetMs = result?.OffsetMs;
        record.Slope = result?.Slope;
        record.Intercept = result?.Intercept;

        plugin.SaveConfiguration();
    }
}
