// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One library's settings as the dashboard sends them back.
/// </summary>
public class LibrarySettingsEntry
{
    /// <summary>
    /// Gets or sets the library's CollectionFolder id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items in this library may be synced.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets which day the scheduled sync runs, as a DayOfWeek name. Null or empty
    /// means every day.
    /// </summary>
    public string? ScheduleDay { get; set; }

    /// <summary>
    /// Gets or sets the time of day the scheduled sync runs, as "HH:mm".
    /// </summary>
    public string? ScheduleTime { get; set; }
}

/// <summary>
/// The whole libraries form, saved in one go.
/// </summary>
public class LibrarySettingsRequest
{
    /// <summary>
    /// Gets the libraries being saved.
    /// </summary>
    public List<LibrarySettingsEntry> Libraries { get; } = new();
}
