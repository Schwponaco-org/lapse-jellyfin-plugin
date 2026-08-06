// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

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
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets the day the scheduled sync runs, or null for every day. Stored as a
    /// nullable enum because "every day" needs a value that isn't a weekday.
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
}
