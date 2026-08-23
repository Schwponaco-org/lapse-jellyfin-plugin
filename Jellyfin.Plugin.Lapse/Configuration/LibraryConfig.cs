// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// Per-library settings. Stored as a list rather than a dictionary for the same reason
/// <see cref="EngineSettings"/> is: Jellyfin serializes plugin config with XmlSerializer,
/// which handles lists cleanly and dictionaries not at all.
/// </summary>
public class LibraryConfig
{
    /// <summary>
    /// Gets or sets the id of the library's CollectionFolder, which is what
    /// ILibraryManager.GetVirtualFolders() hands back as ItemId.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the library name as it was when this entry was saved. Only used so the
    /// scheduler can log something readable without going back to the library manager.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items in this library are eligible for sync.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an item added to this library gets picked
    /// up on its own, without waiting for a scheduled run. Off until it's asked for:
    /// rewriting subtitles across a library is not something that should start happening
    /// because someone installed an update.
    ///
    /// Mutually exclusive with <see cref="ScheduleEnabled"/> - a library is either doing
    /// new items as they arrive or sweeping the lot on a schedule, and having both on
    /// would only mean the schedule redoing work that was already done on arrival.
    /// </summary>
    public bool AutoSyncEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// Mutually exclusive with <see cref="AutoSyncEnabled"/>.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets how often the scheduled sync runs.
    /// </summary>
    public ScheduleFrequency ScheduleFrequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>
    /// Gets or sets the day the scheduled sync runs on, for every frequency except
    /// <see cref="Data.ScheduleFrequency.Daily"/>. Null falls back to Sunday.
    /// </summary>
    public DayOfWeek? ScheduleDay { get; set; }

    /// <summary>
    /// Gets or sets the time of day the scheduled sync runs, as "HH:mm". A string rather
    /// than a TimeSpan because TimeSpan doesn't round trip through XmlSerializer.
    /// </summary>
    public string ScheduleTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets when this library's scheduled sync last fired, so a restart or a
    /// minute-boundary wobble can't run the same slot twice.
    /// </summary>
    public DateTime? LastScheduledRunUtc { get; set; }

    /// <summary>
    /// Parses <see cref="ScheduleTime"/>, falling back to 03:00 if it's been saved as
    /// something unparseable.
    /// </summary>
    /// <returns>The time of day the sync should run.</returns>
    public TimeSpan GetScheduleTimeOfDay()
    {
        if (TimeSpan.TryParse(ScheduleTime, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero
            && parsed < TimeSpan.FromDays(1))
        {
            return parsed;
        }

        return TimeSpan.FromHours(3);
    }

    /// <summary>
    /// Gets the day this library syncs on. Only meaningful for the frequencies that have
    /// a day at all; the daily one runs whatever this says.
    /// </summary>
    /// <returns>The day of the week.</returns>
    public DayOfWeek ResolveScheduleDay()
    {
        return ScheduleDay ?? DayOfWeek.Sunday;
    }

    /// <summary>
    /// Gets how long has to pass after a run before the same schedule may fire again.
    /// Deliberately shorter than the nominal interval so a server that was asleep over
    /// the exact slot still catches the next one rather than skipping a whole period.
    /// </summary>
    /// <returns>The minimum gap between two runs of this schedule.</returns>
    public TimeSpan GetMinimumGap()
    {
        return ScheduleFrequency switch
        {
            ScheduleFrequency.Weekly => TimeSpan.FromDays(6),
            ScheduleFrequency.BiWeekly => TimeSpan.FromDays(13),
            ScheduleFrequency.Monthly => TimeSpan.FromDays(27),
            _ => TimeSpan.FromHours(12)
        };
    }
}
