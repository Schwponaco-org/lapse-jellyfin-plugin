// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/Sync, a single movie sync request.
/// </summary>
public class SyncRequest
{
    /// <summary>
    /// Gets or sets the movie to sync.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets which engine to use. Leave empty to use the configured default.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets the alignment mode to use. Null means "whatever this engine's default
    /// sync mode is set to", which is what the context menu's Sync button and the item
    /// list's Sync button both send.
    /// </summary>
    public SyncMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the penalty value. Only used when <see cref="Mode"/> is Split.
    /// </summary>
    public int Penalty { get; set; }

    /// <summary>
    /// Gets or sets which external subtitle file to sync. Only required when the item
    /// has more than one, otherwise the only one found gets used automatically.
    /// </summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets or sets where the result should land, or null for the configured default.
    /// </summary>
    public OutputMode? OutputMode { get; set; }

    /// <summary>
    /// Gets or sets the format to write the result in - srt, vtt, ass or ssa - or null to
    /// keep whatever the subtitle already was. A subtitle in a format no engine reads is
    /// converted to srt on the way in whatever this says, since there's no other way to
    /// sync it at all.
    /// </summary>
    public string? OutputFormat { get; set; }
}
