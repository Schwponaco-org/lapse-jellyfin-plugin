// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/BulkSync.
/// </summary>
public class BulkSyncRequest
{
    /// <summary>
    /// Gets or sets whether to sync the whole library or just one folder.
    /// </summary>
    public BulkSyncScope Scope { get; set; } = BulkSyncScope.Library;

    /// <summary>
    /// Gets or sets the folder to sync. Required when <see cref="Scope"/> is Folder.
    /// </summary>
    public Guid? FolderId { get; set; }
}
