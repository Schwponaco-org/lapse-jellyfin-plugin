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
    Failed,

    /// <summary>
    /// On the ignore list, so no automatic or bulk run will touch it. Distinct from
    /// <see cref="Skipped"/>, which is a per-item "not now" rather than a standing rule
    /// that also covers everything under a series or folder.
    ///
    /// Last in the enum on purpose: an older config stores these values as numbers, and
    /// inserting anywhere else would silently change what existing records mean.
    /// </summary>
    Ignored
}
