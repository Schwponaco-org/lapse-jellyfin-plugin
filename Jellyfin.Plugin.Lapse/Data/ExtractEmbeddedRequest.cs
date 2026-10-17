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
    /// Gets or sets a value indicating whether to also remove the tracks from the video. Off by default.
    /// </summary>
    public bool RemoveFromVideo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the rebuilt video replaces the original (otherwise it's written as .nosubs).
    /// </summary>
    public bool ReplaceOriginal { get; set; }
}
