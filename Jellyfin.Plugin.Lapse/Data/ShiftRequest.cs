// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// A request to move a subtitle file's timings by hand.
/// </summary>
public class ShiftRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle belongs to, so the path can be checked against
    /// what the library actually lists for it.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle file to move.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how far to move it, in seconds. Negative makes subtitles appear
    /// earlier. Ignored when <see cref="OffsetMs"/> is set, which is what the context
    /// menu's shift dialog uses.
    /// </summary>
    public double OffsetSeconds { get; set; }

    /// <summary>
    /// Gets or sets how far to move it, in milliseconds. Wins over
    /// <see cref="OffsetSeconds"/> when both are given.
    /// </summary>
    public int? OffsetMs { get; set; }

    /// <summary>
    /// Gets or sets where the result should be written, or null for the configured
    /// default.
    /// </summary>
    public OutputMode? OutputMode { get; set; }

    /// <summary>
    /// Gets the offset to actually apply, in seconds.
    /// </summary>
    /// <returns>The offset in seconds.</returns>
    public double ResolveOffsetSeconds()
    {
        return OffsetMs.HasValue ? OffsetMs.Value / 1000.0 : OffsetSeconds;
    }
}
