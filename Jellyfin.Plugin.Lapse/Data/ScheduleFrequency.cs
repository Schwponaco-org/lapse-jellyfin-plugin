// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// How often a library's scheduled sync runs. Everything except <see cref="Daily"/> also
/// picks a day of the week; the daily one only needs a time.
/// </summary>
public enum ScheduleFrequency
{
    /// <summary>
    /// Every day at the configured time.
    /// </summary>
    Daily,

    /// <summary>
    /// Once a week, on the configured day.
    /// </summary>
    Weekly,

    /// <summary>
    /// Every other week, on the configured day.
    /// </summary>
    BiWeekly,

    /// <summary>
    /// Once a month, on the first matching day of the week in the month.
    /// </summary>
    Monthly
}
