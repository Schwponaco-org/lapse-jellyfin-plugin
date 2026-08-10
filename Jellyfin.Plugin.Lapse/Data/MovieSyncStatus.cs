// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Where a movie stands with subtitle syncing.
/// </summary>
public enum MovieSyncStatus
{
    /// <summary>
    /// Not synced yet, and not skipped either.
    /// </summary>
    Pending,

    /// <summary>
    /// Every external subtitle the item currently has been synced.
    /// </summary>
    Synced,

    /// <summary>
    /// Some of the item's external subtitles have been synced and some haven't. Usually a
    /// track that was added after the last sync, or a sync that only ran on one of them.
    /// </summary>
    PartiallySynced,

    /// <summary>
    /// Marked as skip, either directly or because a parent folder is skipped.
    /// </summary>
    Skipped,

    /// <summary>
    /// Last sync attempt failed. See <see cref="MovieSyncRecord.LastError"/> for why.
    /// </summary>
    Failed
}
