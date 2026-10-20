// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What unattended runs (schedules, auto-sync, bulk, webhook) do to the subtitles they find.
/// </summary>
public enum AutomationAction
{
    /// <summary>
    /// Just sync, keep the format. The default, LAPSE reads and writes every common format anyway.
    /// </summary>
    Sync = 0,

    /// <summary>
    /// Only write the subtitles out in the conversion format, without syncing them.
    /// For getting a library onto one format and doing the timing separately.
    /// </summary>
    Convert = 1,

    /// <summary>
    /// Convert to the conversion format, then sync the converted file.
    /// </summary>
    ConvertThenSync = 2
}
