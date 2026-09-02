// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Asks for an item's embedded subtitle tracks to be written out as files, and says how
/// far to go with the video afterwards.
/// </summary>
public class ExtractEmbeddedRequest
{
    /// <summary>
    /// Gets or sets the item to work on.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the extracted tracks should then be taken
    /// out of the video file. Off by default: extracting alone adds files and removes
    /// nothing, which is the answer that can't go wrong.
    /// </summary>
    public bool RemoveFromVideo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the rebuilt video replaces the original.
    /// Off by default, which leaves the original where it is and writes the rebuilt one
    /// beside it as a .nosubs file. Only means anything with
    /// <see cref="RemoveFromVideo"/> set.
    /// </summary>
    public bool ReplaceOriginal { get; set; }
}
