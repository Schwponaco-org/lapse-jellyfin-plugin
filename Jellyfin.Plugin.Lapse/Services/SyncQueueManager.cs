// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Runs bulk (and auto-sync) subtitle sync jobs one movie at a time in the background,
/// so the dashboard doesn't have to wait around for a whole library to finish.
/// Bulk and auto-sync jobs always run standard (OLS) mode against every external
/// subtitle a movie has - there's no UI in the background path to pick mode/penalty/subtitle.
/// </summary>
public class SyncQueueManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly LapseEngineClient _engineClient;
    private readonly ILogger<SyncQueueManager> _logger;
    private readonly object _lock = new();
    private readonly List<QueueItem> _items = new();
    private readonly Queue<Guid> _pending = new();
    private readonly HashSet<Guid> _queuedIds = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueueManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up movies and folders.</param>
    /// <param name="subtitleLocator">Finds external subtitles for a movie.</param>
    /// <param name="engineClient">Runs the actual LAPSE engine.</param>
    /// <param name="logger">Logger.</param>
    public SyncQueueManager(
        ILibraryManager libraryManager,
        SubtitleLocator subtitleLocator,
        LapseEngineClient engineClient,
        ILogger<SyncQueueManager> logger)
    {
        _libraryManager = libraryManager;
        _subtitleLocator = subtitleLocator;
        _engineClient = engineClient;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a movie or folder id is skipped (either directly,
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
    /// Starts a bulk sync job for every movie in every library. Does nothing (and returns
    /// false) if a job is already running, since we only ever run one at a time.
    /// </summary>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueLibrary()
    {
        var movies = GetMovies(null);
        return StartBulkJob(movies);
    }

    /// <summary>
    /// Starts a bulk sync job for every movie under one folder.
    /// </summary>
    /// <param name="folderId">The folder to sync.</param>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueFolder(Guid folderId)
    {
        var movies = GetMovies(folderId);
        return StartBulkJob(movies);
    }

    /// <summary>
    /// Adds one movie to whatever's already queued, starting the worker if it isn't running.
    /// Used by auto-sync when a new movie shows up. Skipped movies are silently ignored.
    /// </summary>
    /// <param name="movie">The movie to sync.</param>
    public void EnqueueMovie(BaseItem movie)
    {
        if (IsSkipped(movie))
        {
            return;
        }

        lock (_lock)
        {
            if (!_queuedIds.Add(movie.Id))
            {
                return;
            }

            _pending.Enqueue(movie.Id);
            _items.Add(new QueueItem { ItemId = movie.Id, Name = movie.Name ?? "Unknown" });
        }

        EnsureWorkerRunning();
    }

    private bool StartBulkJob(IReadOnlyList<BaseItem> movies)
    {
        lock (_lock)
        {
            if (_pending.Count > 0 || _items.Any(i => i.Status == QueueItemStatus.Running))
            {
                return false;
            }

            _items.Clear();
            _queuedIds.Clear();

            foreach (var movie in movies)
            {
                _queuedIds.Add(movie.Id);
                _pending.Enqueue(movie.Id);
                _items.Add(new QueueItem { ItemId = movie.Id, Name = movie.Name ?? "Unknown" });
            }
        }

        if (movies.Count == 0)
        {
            return false;
        }

        EnsureWorkerRunning();
        return true;
    }

    private List<BaseItem> GetMovies(Guid? folderId)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        };

        if (folderId.HasValue)
        {
            query.ParentId = folderId.Value;
        }

        return _libraryManager.GetItemList(query).Where(m => !IsSkipped(m)).ToList();
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

            await ProcessOneAsync(itemId).ConfigureAwait(false);
        }
    }

    private async Task ProcessOneAsync(Guid itemId)
    {
        SetItemStatus(itemId, QueueItemStatus.Running);

        var movie = _libraryManager.GetItemById(itemId);
        if (movie is null || string.IsNullOrEmpty(movie.Path))
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "Movie not found or has no video file");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return;
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(movie);
        if (subtitles.Count == 0)
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "No external subtitle found");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return;
        }

        string? lastError = null;
        SyncResult? lastResult = null;

        foreach (var subtitle in subtitles)
        {
            var result = await _engineClient.RunAsync(movie.Path, subtitle.Path, SyncMode.Standard, 0).ConfigureAwait(false);
            lastResult = result;

            if (!result.Success)
            {
                lastError = result.Error;
                _logger.LogWarning("LAPSE sync failed for {Movie} ({Subtitle}): {Error}", movie.Name, subtitle.Path, result.Error);
            }
        }

        var status = lastError is null ? MovieSyncStatus.Synced : MovieSyncStatus.Failed;
        SaveRecord(itemId, status, lastError, lastResult);
        SetItemStatus(itemId, status == MovieSyncStatus.Synced ? QueueItemStatus.Done : QueueItemStatus.Failed);
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
    /// Saves (or updates) a movie's sync record and persists the plugin config to disk.
    /// Shared by the background queue and the controller's synchronous single-item sync.
    /// </summary>
    /// <param name="itemId">The movie.</param>
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
