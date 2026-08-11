// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One entry on the ignore list. Either a library item (a film, a series, a season, a
/// folder) by id, or a path on disk covering everything under it.
///
/// Both shapes exist because they answer different questions. Picking a series out of the
/// library is what you want for "never touch this show", and it survives the files being
/// moved. A path is what you want for "never touch anything in here", including things
/// Jellyfin hasn't scanned yet, which is the case that matters for the Radarr and Sonarr
/// webhook.
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
    /// Gets or sets what kind of thing this is - Movie, Series, Folder and so on - purely
    /// for the dashboard listing.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets when the rule was added.
    /// </summary>
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}
