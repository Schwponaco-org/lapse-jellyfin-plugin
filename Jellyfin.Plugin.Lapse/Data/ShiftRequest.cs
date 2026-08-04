// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/Shift, the manual nudge for when a sync lands close but not perfect.
/// </summary>
public class ShiftRequest
{
    /// <summary>
    /// Gets or sets the movie the subtitle belongs to. The subtitle has to actually be one
    /// of this movie's external subtitles, so we're never writing to some arbitrary path
    /// somebody passed in.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle file to nudge.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many seconds to move it. Positive makes subtitles show up later,
    /// negative makes them show up earlier.
    /// </summary>
    public double OffsetSeconds { get; set; }
}
