// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One row in the dashboard's sync status list. This is the live library listing joined
/// up with whatever sync history we've got stored for each item.
/// </summary>
public class ItemStatusEntry
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name to show, including series and episode number for episodes.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what kind of item this is - Movie, Episode, Video and so on.
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library the item lives in.
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>
    /// Gets or sets the id of the library the item lives in, for the dashboard filter.
    /// </summary>
    public Guid? LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public MovieSyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when it was last synced, if ever.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the last error message, if the last attempt failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets how many external subtitles the item has. Libraries that scan in
    /// unrelated video files (phone backups, personal clips, whatever) tend to fill up
    /// with items that have none, so the dashboard hides those by default.
    /// </summary>
    public int SubtitleCount { get; set; }

    /// <summary>
    /// Gets or sets how many of those subtitles have actually been synced. Less than
    /// <see cref="SubtitleCount"/> but more than zero is what "partially synced" means.
    /// </summary>
    public int SyncedSubtitleCount { get; set; }

    /// <summary>
    /// Gets or sets the item's file path, so the dashboard can offer to add it to the
    /// ignore list by path as well as by id.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets a value indicating whether this item has at least one external subtitle.
    /// </summary>
    public bool HasExternalSubtitle => SubtitleCount > 0;
}
