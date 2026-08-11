// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// "Sync every other subtitle on this item to this one." Handy when one subtitle track is
/// known good and the rest are off: lining the others up against a subtitle is both
/// faster and more accurate than going back to the audio for each of them.
/// </summary>
public class MultiSubtitleSyncRequest
{
    /// <summary>
    /// Gets or sets the item whose subtitles are being synced.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle everything else gets lined up against.
    /// </summary>
    public string ReferencePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which engine to use, or null for the configured default.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets the alignment mode, or null for the engine's own default.
    /// </summary>
    public SyncMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the penalty for split mode, or null for the engine's default.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets where the results should land, or null for the configured default.
    /// </summary>
    public OutputMode? OutputMode { get; set; }
}

/// <summary>
/// What happened to one subtitle in a multi-track sync.
/// </summary>
public class SubtitleSyncOutcome
{
    /// <summary>
    /// Gets or sets the subtitle that was synced.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name shown in the dashboard.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the engine result.
    /// </summary>
    public SyncResult? Result { get; set; }
}

/// <summary>
/// The whole multi-track sync, one entry per non-reference subtitle.
/// </summary>
public class MultiSubtitleSyncResult
{
    /// <summary>
    /// Gets or sets the reference subtitle everything was lined up against.
    /// </summary>
    public string ReferencePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many subtitles synced without an error.
    /// </summary>
    public int SucceededCount { get; set; }

    /// <summary>
    /// Gets the per-subtitle results.
    /// </summary>
    public List<SubtitleSyncOutcome> Results { get; } = new();
}
