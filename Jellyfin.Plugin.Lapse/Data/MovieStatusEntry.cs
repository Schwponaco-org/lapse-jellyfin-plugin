// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One row in the dashboard's sync status list. This is the library's live movie list
/// joined up with whatever sync history we've got stored for it.
/// </summary>
public class MovieStatusEntry
{
    /// <summary>
    /// Gets or sets the movie's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the movie name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

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
}
