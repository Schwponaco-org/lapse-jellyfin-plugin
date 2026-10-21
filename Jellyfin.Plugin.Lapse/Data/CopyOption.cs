// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Another copy of the same film or episode that a subtitle can be moved from.
/// </summary>
public class CopyOption
{
    /// <summary>
    /// Gets or sets the copy's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the video's file name, which is what tells two versions apart.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where it is: "another version of this item" or the library it's in.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many subtitles the copy has that LAPSE can read.
    /// </summary>
    public int SubtitleCount { get; set; }
}

/// <summary>
/// Answer to GET /Lapse/Items/{id}/Copies.
/// </summary>
public class CopyList
{
    /// <summary>
    /// Gets or sets a value indicating whether the installed LAPSE can move a subtitle between copies.
    /// </summary>
    public bool CanMove { get; set; }

    /// <summary>
    /// Gets or sets why it can't, when it can't.
    /// </summary>
    public string? Problem { get; set; }

    /// <summary>
    /// Gets the other copies.
    /// </summary>
    public List<CopyOption> Copies { get; } = new();
}
