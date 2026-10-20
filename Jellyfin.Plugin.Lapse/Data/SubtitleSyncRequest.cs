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
    /// Gets or sets which engine to use. Leave empty to use the configured default.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets the path to the subtitle file used as the reference (the one that's correct).
    /// </summary>
    public string ReferencePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the subtitle file that needs to be lined up.
    /// </summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the result should land, or null for the configured default.
    /// Ignored when <see cref="OutputPath"/> names a file to write.
    /// </summary>
    public OutputMode? OutputMode { get; set; }

    /// <summary>
    /// Gets or sets a file to write to instead of the input. A bare file name goes where <see cref="Placement"/> says.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets which folder a bare <see cref="OutputPath"/> file name lands in, or
    /// null for the configured default.
    /// </summary>
    public SubToSubPlacement? Placement { get; set; }
}
