// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Scope for a bulk sync job.
/// </summary>
public enum BulkSyncScope
{
    /// <summary>
    /// Every movie in every library.
    /// </summary>
    Library,

    /// <summary>
    /// Just the movies inside one folder.
    /// </summary>
    Folder
}
