// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/SyncFromCopy. Takes a subtitle that fits one copy of a film (a TV
/// recording, the theatrical cut) and moves it onto another by lining up their soundtracks.
/// </summary>
public class CopySyncRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle should end up on.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the copy the subtitle already fits. Has to be one of the copies
    /// GET /Lapse/Items/{id}/Copies lists for <see cref="ItemId"/>.
    /// </summary>
    public Guid SourceItemId { get; set; }

    /// <summary>
    /// Gets or sets one of the source copy's subtitles, a file path or an embedded:// key.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the alignment mode, or null for LAPSE's default.
    /// </summary>
    public SyncMode? Mode { get; set; }
}
