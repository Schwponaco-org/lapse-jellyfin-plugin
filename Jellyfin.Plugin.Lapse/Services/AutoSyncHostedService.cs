// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Queues newly added items for a sync (per library, off by default). Debounced so a big
/// scan doesn't kick off a sync per item before its subtitles have even landed.
///
/// Also queues an item that's already in the library when a new subtitle file turns up for it,
/// e.g. one Jellyfin downloaded itself. That comes in as an item update, which fires for all
/// sorts of reasons and again after LAPSE writes its own files, so only a subtitle written
/// since the item's last sync counts.
/// </summary>
public sealed class AutoSyncHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<AutoSyncHostedService> _logger;
    private readonly Timer _debounceTimer;
    private readonly Timer _updateTimer;
    private readonly object _lock = new();
    private readonly HashSet<Guid> _pendingItemIds = new();
    private readonly HashSet<Guid> _updatedItemIds = new();

    // Each subtitle version gets queued once. Stops a loop on a share whose clock runs ahead,
    // where an unsure subtitle would look newer than every sync of it.
    private readonly HashSet<(string Path, DateTime Written)> _queuedSubtitles = new();
    private DateTime _startedUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoSyncHostedService"/> class.
    /// </summary>
    public AutoSyncHostedService(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SyncQueueManager queueManager,
        ILogger<AutoSyncHostedService> logger)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _queueManager = queueManager;
        _logger = logger;
        _debounceTimer = new Timer(OnTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _updateTimer = new Timer(OnUpdateTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedUtc = DateTime.UtcNow;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _debounceTimer.Dispose();
        _updateTimer.Dispose();
    }

    /// <summary>
    /// Lists the item's external subtitles that are new since <paramref name="since"/> or its
    /// last sync, whichever is later. Leaves out what LAPSE wrote: synced files, sidecars,
    /// scratch files and extracted tracks nobody synced. Reads Jellyfin's streams, not the folder.
    /// </summary>
    internal static List<string> NewSubtitles(BaseItem item, MovieSyncRecord? record, DateTime since)
    {
        if (record?.LastSyncUtc > since)
        {
            since = record.LastSyncUtc.Value;
        }

        var paths = item.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && SubtitleFormats.IsRealSubtitle(s.Path))
            .Select(s => s.Path!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // a missing file reads as 1601, so it never counts as new
        return paths.FindAll(path =>
            File.GetLastWriteTimeUtc(path) > since
            && !EngineRunner.IsSidecarOfAnother(path, paths)
            && !SubtitleExtractor.IsUntouchedExtraction(item, path, record)
            && (record is null || !SyncQueueManager.IsStillSynced(record, path)));
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        var item = itemChangeEventArgs.Item;

        // Nobody has auto-sync on (the default), so don't collect anything during scans.
        // The real library check waits for the timer, mid-scan the parents aren't always
        // wired up yet and it can come back empty.
        if (item.LocationType == LocationType.Virtual
            || !LibraryService.IsSyncableKind(item)
            || Plugin.Instance?.Configuration.Libraries.Exists(l => l.AutoSyncEnabled) != true)
        {
            return;
        }

        lock (_lock)
        {
            _pendingItemIds.Add(item.Id);
        }

        _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        var item = itemChangeEventArgs.Item;

        // this fires on Jellyfin's own thread for every saved item, so only the cheap checks here
        if (item.LocationType == LocationType.Virtual
            || !LibraryService.IsSyncableKind(item)
            || Plugin.Instance?.Configuration is not { AutoSyncNewSubtitles: true } config
            || !config.Libraries.Exists(l => l.AutoSyncEnabled))
        {
            return;
        }

        lock (_lock)
        {
            // A fixed window from the first update, not pushed back by each one. A long
            // metadata or chapter image task keeps updating items for hours.
            if (_updatedItemIds.Add(item.Id) && _updatedItemIds.Count == 1)
            {
                _updateTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnUpdateTimerElapsed(object? state)
    {
        try
        {
            QueueUpdated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-sync could not queue the new subtitles");
        }
    }

    private void OnTimerElapsed(object? state)
    {
        // an exception out of a timer callback takes the whole server down
        try
        {
            QueuePending();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-sync could not queue the newly added items");
        }
    }

    private void QueuePending()
    {
        List<Guid> itemIds;
        lock (_lock)
        {
            itemIds = new List<Guid>(_pendingItemIds);
            _pendingItemIds.Clear();
        }

        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                continue;
            }

            if (!_libraryService.IsAutoSyncEnabled(item))
            {
                _logger.LogDebug(
                    "Auto-sync skipping {Item}: its library is turned off, has auto-sync turned off, or it's on the skip list",
                    item.Name);
                continue;
            }

            _logger.LogInformation("Auto-sync queuing newly added item: {Item}", SyncQueueManager.DescribeItem(item));
            _queueManager.EnqueueItem(item);
        }
    }

    private void QueueUpdated()
    {
        List<Guid> itemIds;
        lock (_lock)
        {
            // a newly added item gets its whole sync from the other timer
            itemIds = _updatedItemIds.Where(id => !_pendingItemIds.Contains(id)).ToList();
            _updatedItemIds.Clear();
        }

        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || _queueManager.IsQueuedOrRunning(itemId) || !_libraryService.IsAutoSyncEnabled(item))
            {
                continue;
            }

            var written = NewSubtitles(item, SyncQueueManager.FindRecord(itemId), _startedUtc)
                .ConvertAll(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)));

            lock (_lock)
            {
                written.RemoveAll(w => !_queuedSubtitles.Add(w));
            }

            if (written.Count == 0)
            {
                continue;
            }

            _logger.LogInformation(
                "Auto-sync queuing {Item} for its new subtitle {Subtitle}",
                SyncQueueManager.DescribeItem(item),
                Path.GetFileName(written[0].Path));
            _queueManager.EnqueueItem(item);
        }
    }
}
