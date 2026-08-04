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
    /// Synced successfully at least once.
    /// </summary>
    Synced,

    /// <summary>
    /// Marked as skip, either directly or because a parent folder is skipped.
    /// </summary>
    Skipped,

    /// <summary>
    /// Last sync attempt failed. See <see cref="MovieSyncRecord.LastError"/> for why.
    /// </summary>
    Failed
}
