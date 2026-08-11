// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One line of recent sync history for the dashboard's activity list.
/// </summary>
public class RecentActivityEntry
{
    /// <summary>
    /// Gets or sets the history entry id, which is what a revert names.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the item id, so the row can be clicked through to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets what to call the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how it went.
    /// </summary>
    public MovieSyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when.
    /// </summary>
    public DateTime? WhenUtc { get; set; }

    /// <summary>
    /// Gets or sets a short line about what happened - the offset found, or why it failed.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Gets or sets the file that was written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this has already been put back.
    /// </summary>
    public bool Reverted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether undoing this would still do something.
    /// </summary>
    public bool CanRevert { get; set; }
}

/// <summary>
/// What the dashboard's front page needs beyond the item list it already has: which
/// engine is doing the work, and what has been synced lately.
///
/// The library counts are not here on purpose. The browser already holds the full status
/// list for the Sync status panel, so it adds those up itself rather than making the
/// server walk every item and stat its subtitles a second time.
/// </summary>
public class DashboardOverview
{
    /// <summary>
    /// Gets or sets the id of the engine syncs use by default.
    /// </summary>
    public string? ActiveEngineId { get; set; }

    /// <summary>
    /// Gets or sets the name of that engine.
    /// </summary>
    public string? ActiveEngineName { get; set; }

    /// <summary>
    /// Gets or sets the version of that engine on disk.
    /// </summary>
    public string? ActiveEngineVersion { get; set; }

    /// <summary>
    /// Gets or sets the mode a plain Sync press runs in with that engine.
    /// </summary>
    public string? ActiveEngineMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether that engine is installed and starts.
    /// </summary>
    public bool ActiveEngineReady { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any engine at all is installed. When
    /// nothing is, the dashboard drops back to a single Install button for LAPSE rather
    /// than showing counts that would all be zero.
    /// </summary>
    public bool AnyEngineInstalled { get; set; }

    /// <summary>
    /// Gets the last handful of syncs, newest first.
    /// </summary>
    public List<RecentActivityEntry> Recent { get; } = new();
}
