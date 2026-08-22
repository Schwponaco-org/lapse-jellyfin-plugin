// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What a run nobody is watching does to the subtitles it finds. This covers the
/// scheduled task, the per-library schedules, the auto-sync on a newly added item, the
/// bulk runs and the Radarr/Sonarr webhook - everything that isn't a press on a button.
/// </summary>
public enum AutomationAction
{
    /// <summary>
    /// Line the subtitles up and leave their format alone. The default, and with LAPSE
    /// the only thing most libraries need: it reads and writes every common format, so
    /// there's nothing to convert on the way past.
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
