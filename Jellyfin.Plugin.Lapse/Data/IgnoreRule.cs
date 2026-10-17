// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One ignore list entry: a library item by id (survives files moving) or a path (covers stuff
/// Jellyfin hasn't scanned yet, which matters for the webhook).
/// </summary>
public class IgnoreRule
{
    /// <summary>
    /// Gets or sets the item this covers, or null for a path rule.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the path this covers, or null for an item rule.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets what to show in the list, so a rule for an item that has since been
    /// removed from the library is still recognisable rather than a bare id.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what kind of thing it is (Movie, Series, Folder...), for the list.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets when the rule was added.
    /// </summary>
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What taking something off the ignore list actually did.
/// </summary>
public class IgnoreRemovalResult
{
    /// <summary>
    /// Gets or sets how many rules were removed. Zero means it's covered by a rule higher up (series, folder).
    /// </summary>
    public int Removed { get; set; }
}
