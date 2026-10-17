// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Queues newly added items for a sync (per library, off by default). Debounced so a big
/// scan doesn't kick off a sync per item before its subtitles have even landed.
/// </summary>
public sealed class AutoSyncHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<AutoSyncHostedService> _logger;
    private readonly Timer _debounceTimer;
    private readonly object _lock = new();
    private readonly HashSet<Guid> _pendingItemIds = new();

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
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _debounceTimer.Dispose();
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
}
