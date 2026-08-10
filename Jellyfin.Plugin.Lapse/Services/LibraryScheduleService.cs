// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Fires the per-library scheduled syncs. Jellyfin's own scheduler attaches triggers to
/// a task rather than to a library, so "sync the TV library on Sundays at 02:00 and the
/// films on Wednesdays at 04:00" can't be expressed with task triggers alone. This is a
/// one minute tick that looks at each library's own day and time and queues its items
/// when the slot comes round. The whole-everything daily run is still a normal scheduled
/// task, see LibrarySyncTask.
/// </summary>
public sealed class LibraryScheduleService : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // How long after the configured time a slot still counts as "now". Wide enough that a
    // server which was asleep or busy for a few minutes doesn't skip the day entirely.
    private static readonly TimeSpan SlotWindow = TimeSpan.FromMinutes(10);

    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<LibraryScheduleService> _logger;
    private readonly Timer _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScheduleService"/> class.
    /// </summary>
    /// <param name="libraryService">Finds the items in a library.</param>
    /// <param name="queueManager">Where scheduled items get queued.</param>
    /// <param name="logger">Logger.</param>
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
        // First tick goes in almost straight away rather than a minute out, so a server
        // that was off over a slot picks it up on startup instead of waiting.
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
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        // Schedules are wall clock times the user typed into the dashboard, so they're
        // compared against local time. Only the "last run" bookkeeping is stored in UTC.
        var now = DateTime.Now;
        var saveNeeded = false;

        // ToArray because enqueuing can end up touching the configuration, and iterating
        // the live list while that happens throws.
        foreach (var library in plugin.Configuration.Libraries.ToArray())
        {
            if (!library.Enabled || !library.ScheduleEnabled)
            {
                continue;
            }

            if (!IsDue(library, now))
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

            foreach (var item in items)
            {
                _queueManager.EnqueueItem(item);
            }
        }

        if (saveNeeded)
        {
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Says whether a library's schedule should fire right now.
    ///
    /// The day and time are wall clock, and the "has it already run" guard is a minimum
    /// gap rather than a calendar calculation. That's deliberate: a gap slightly shorter
    /// than the nominal interval means a server that was asleep across the exact slot
    /// still catches the following one, instead of silently skipping a whole month.
    /// </summary>
    /// <param name="library">The library's settings.</param>
    /// <param name="now">Local time now.</param>
    /// <returns>True if the sync is due.</returns>
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

        // don't run the same slot twice, whether that's two ticks inside the window or a
        // restart that lands back in it
        if (library.LastScheduledRunUtc.HasValue
            && DateTime.UtcNow - library.LastScheduledRunUtc.Value < library.GetMinimumGap())
        {
            return false;
        }

        return true;
    }
}
