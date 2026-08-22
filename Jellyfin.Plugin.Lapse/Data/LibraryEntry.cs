// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One library as the dashboard sees it: what it is, whether LAPSE touches it, and when.
/// </summary>
public class LibraryEntry
{
    /// <summary>
    /// Gets or sets the library's CollectionFolder id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what kind of library it is (movies, tvshows, homevideos, or null for
    /// a mixed one). Only used to label the row.
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items in this library may be synced.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly added items in this library are
    /// picked up on their own, without waiting for a scheduled run.
    /// </summary>
    public bool AutoSyncEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets how often the scheduled sync runs, as a ScheduleFrequency name.
    /// </summary>
    public string ScheduleFrequency { get; set; } = "Daily";

    /// <summary>
    /// Gets or sets which day the scheduled sync runs, as a DayOfWeek name. Not used by
    /// the daily frequency.
    /// </summary>
    public string? ScheduleDay { get; set; }

    /// <summary>
    /// Gets or sets the time of day the scheduled sync runs, as "HH:mm".
    /// </summary>
    public string ScheduleTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets a value indicating whether the whole library is on the skip list.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library holds series, which is what
    /// decides whether the dashboard offers it the series and season sync buttons.
    /// </summary>
    public bool IsShowLibrary { get; set; }

    /// <summary>
    /// Gets or sets when this library's schedule last fired, so the dashboard can say
    /// whether it's actually running.
    /// </summary>
    public DateTime? LastScheduledRunUtc { get; set; }
}
