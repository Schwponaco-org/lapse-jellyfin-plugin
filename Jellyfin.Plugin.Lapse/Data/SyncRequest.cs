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
    /// Gets or sets the alignment mode to use.
    /// </summary>
    public SyncMode Mode { get; set; } = SyncMode.Ols;

    /// <summary>
    /// Gets or sets the penalty value. Only used when <see cref="Mode"/> is Split.
    /// </summary>
    public int Penalty { get; set; }

    /// <summary>
    /// Gets or sets which external subtitle file to sync. Only required when the movie
    /// has more than one, otherwise the only one found gets used automatically.
    /// </summary>
    public string? SubtitlePath { get; set; }
}
