// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Watches for new movies getting added to the library and queues them up for a sync,
/// same as the classic intro-skipper auto-detect hook. Debounced with a timer so a big
/// library scan that adds a bunch of movies at once doesn't kick off a sync per movie
/// the instant each one shows up, before its subtitles have even settled.
/// </summary>
public sealed class AutoSyncHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<AutoSyncHostedService> _logger;
    private readonly Timer _debounceTimer;
    private readonly object _lock = new();
    private readonly HashSet<Guid> _pendingMovieIds = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoSyncHostedService"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to subscribe to ItemAdded and look items back up.</param>
    /// <param name="queueManager">Where newly added movies get queued for syncing.</param>
    /// <param name="logger">Logger.</param>
    public AutoSyncHostedService(ILibraryManager libraryManager, SyncQueueManager queueManager, ILogger<AutoSyncHostedService> logger)
    {
        _libraryManager = libraryManager;
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

        if (item is not Movie || item.LocationType == LocationType.Virtual)
        {
            return;
        }

        lock (_lock)
        {
            _pendingMovieIds.Add(item.Id);
        }

        _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerElapsed(object? state)
    {
        List<Guid> movieIds;
        lock (_lock)
        {
            movieIds = new List<Guid>(_pendingMovieIds);
            _pendingMovieIds.Clear();
        }

        foreach (var movieId in movieIds)
        {
            var movie = _libraryManager.GetItemById(movieId);
            if (movie is null)
            {
                continue;
            }

            _logger.LogInformation("Auto-sync queuing newly added movie: {Movie}", movie.Name);
            _queueManager.EnqueueMovie(movie);
        }
    }
}
