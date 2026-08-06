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
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets which day the scheduled sync runs, as a DayOfWeek name, or null for
    /// every day.
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
}
