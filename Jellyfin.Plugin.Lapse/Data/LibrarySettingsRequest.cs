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
    /// Gets or sets a value indicating whether newly added items in this library are
    /// picked up on their own.
    /// </summary>
    public bool AutoSyncEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this library gets synced on a schedule.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Gets or sets how often the scheduled sync runs, as a
    /// <see cref="ScheduleFrequency"/> name. Null or empty means every day.
    /// </summary>
    public string? ScheduleFrequency { get; set; }

    /// <summary>
    /// Gets or sets which day the scheduled sync runs, as a DayOfWeek name. Ignored for
    /// the daily frequency, which has no day to pick.
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
    /// Gets or sets the libraries being saved.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "This needs a setter. System.Text.Json - which is what ASP.NET Core binds request bodies with - skips collection properties it can't assign to rather than adding into them, so a read-only collection here silently arrives empty. That is exactly what made saving the library toggles look like it worked while doing nothing at all.")]
    public List<LibrarySettingsEntry> Libraries { get; set; } = new();
}
