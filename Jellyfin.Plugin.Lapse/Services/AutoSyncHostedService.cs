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
/// Watches for new items getting added to a library and queues them up for a sync,
/// same as the classic intro-skipper auto-detect hook. Debounced with a timer so a big
/// library scan that adds a bunch of items at once doesn't kick off a sync per item
/// the instant each one shows up, before its subtitles have even settled.
///
/// This is what means adding a film doesn't have to wait for the next scheduled run. It
/// is per library, so a library can be on for scheduled and bulk runs and still not have
/// every new file picked up the moment it lands.
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
    /// <param name="libraryManager">Used to subscribe to ItemAdded and look items back up.</param>
    /// <param name="libraryService">Decides whether an item's library is turned on.</param>
    /// <param name="queueManager">Where newly added items get queued for syncing.</param>
    /// <param name="logger">Logger.</param>
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

        // The library check happens on the timer rather than here: during a scan the item's
        // parents aren't always wired up yet, so asking which library it's in this early
        // can come back empty for something that is perfectly fine 30 seconds later.
        if (item.LocationType == LocationType.Virtual || !LibraryService.IsSyncableKind(item))
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
