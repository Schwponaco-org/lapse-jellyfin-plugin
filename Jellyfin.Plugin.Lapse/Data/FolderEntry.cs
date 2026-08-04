// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// A top level library folder, for the bulk sync folder dropdown.
/// </summary>
public class FolderEntry
{
    /// <summary>
    /// Gets or sets the folder's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the folder name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this folder is currently skipped.
    /// </summary>
    public bool Skipped { get; set; }
}
