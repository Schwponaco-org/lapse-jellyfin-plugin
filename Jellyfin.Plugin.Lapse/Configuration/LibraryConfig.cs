// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// Per-library settings.
/// </summary>
public class LibraryConfig
{
    /// <summary>
    /// Gets or sets the CollectionFolder id (ItemId from GetVirtualFolders).
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the library name when saved. Only used for log lines.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items in this library are eligible for sync.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether new items get synced as they're added.
    /// Off by default. Can't be on together with <see cref="ScheduleEnabled"/>.
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
    /// Gets or sets the day for non-daily schedules. Null means Sunday.
    /// </summary>
    public DayOfWeek? ScheduleDay { get; set; }

    /// <summary>
    /// Gets or sets the time of day as "HH:mm" (TimeSpan doesn't survive XmlSerializer).
    /// </summary>
    public string ScheduleTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets when the schedule last fired, so the same slot can't run twice.
    /// </summary>
    public DateTime? LastScheduledRunUtc { get; set; }

    /// <summary>
    /// Parses <see cref="ScheduleTime"/>, 03:00 if it's garbage.
    /// </summary>
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
    /// Gets the schedule day (ignored for daily).
    /// </summary>
    public DayOfWeek ResolveScheduleDay()
    {
        return ScheduleDay ?? DayOfWeek.Sunday;
    }

    /// <summary>
    /// Gets the minimum gap between two runs. A bit shorter than the real interval so a
    /// server that slept through the slot still catches the next one.
    /// </summary>
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
