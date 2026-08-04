// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/SyncSubtitles. Subtitle to subtitle sync, always standard (OLS) mode.
/// </summary>
public class SubtitleSyncRequest
{
    /// <summary>
    /// Gets or sets the path to the subtitle file used as the reference (the one that's correct).
    /// </summary>
    public string ReferencePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the subtitle file that needs to be lined up.
    /// </summary>
    public string InputPath { get; set; } = string.Empty;
}
