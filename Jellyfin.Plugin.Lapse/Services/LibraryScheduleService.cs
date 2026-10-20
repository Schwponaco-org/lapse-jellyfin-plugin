// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Per-library schedules. Jellyfin's task triggers are per task, not per library, so
/// "TV on Sundays 02:00, films Wednesdays 04:00" needs this: a 1 minute tick that queues a
/// library when its slot comes round. The everything-daily run is LibrarySyncTask.
/// </summary>
public sealed class LibraryScheduleService : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // how late a slot can still fire, so a server that was busy for a few minutes doesn't skip the day
    private static readonly TimeSpan SlotWindow = TimeSpan.FromMinutes(10);

    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<LibraryScheduleService> _logger;
    private readonly Timer _timer;
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScheduleService"/> class.
    /// </summary>
    public LibraryScheduleService(LibraryService libraryService, SyncQueueManager queueManager, ILogger<LibraryScheduleService> logger)
    {
        _libraryService = libraryService;
        _queueManager = queueManager;
        _logger = logger;
        _timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // first tick soon after startup so a slot missed while the server was off gets picked up
        _timer.Change(TimeSpan.FromSeconds(20), TickInterval);
        _logger.LogInformation("LAPSE per-library schedules are running, checking every {Interval}", TickInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
    }

    private void OnTick(object? state)
    {
        // a big library can take longer than a tick, don't overlap
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        // an exception out of a timer callback takes the server down
        try
        {
            QueueDueLibraries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the per-library sync schedules failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void QueueDueLibraries()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        // schedules are wall clock times, only the last-run bookkeeping is UTC
        var now = DateTime.Now;
        var saveNeeded = false;

        LibraryConfig[] libraries;
        lock (Plugin.ConfigurationLock)
        {
            libraries = plugin.Configuration.Libraries.ToArray();
        }

        foreach (var library in libraries)
        {
            if (!library.Enabled || !library.ScheduleEnabled || !IsDue(library, now))
            {
                continue;
            }

            library.LastScheduledRunUtc = DateTime.UtcNow;
            saveNeeded = true;

            var items = _libraryService.GetItems(library.LibraryId);
            _logger.LogInformation(
                "Scheduled sync for library {Library} ({Frequency} at {Time}) queuing {Count} items",
                library.Name ?? library.LibraryId.ToString(),
                library.ScheduleFrequency,
                library.ScheduleTime,
                items.Count);

            _queueManager.EnqueueRange(items);
        }

        if (saveNeeded)
        {
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Says whether a library's schedule should fire now. "Already ran" is a minimum gap a
    /// bit shorter than the interval, so a server asleep over the exact slot still catches the next.
    /// </summary>
    internal static bool IsDue(LibraryConfig library, DateTime now)
    {
        if (library.ScheduleFrequency != Data.ScheduleFrequency.Daily
            && now.DayOfWeek != library.ResolveScheduleDay())
        {
            return false;
        }

        var since = now.TimeOfDay - library.GetScheduleTimeOfDay();
        if (since < TimeSpan.Zero || since > SlotWindow)
        {
            return false;
        }

        // same slot twice (two ticks in the window, or a restart inside it)
        return !library.LastScheduledRunUtc.HasValue
            || DateTime.UtcNow - library.LastScheduledRunUtc.Value >= library.GetMinimumGap();
    }
}
