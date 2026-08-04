// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// LAPSE settings, saved to disk as XML by Jellyfin's usual plugin config machinery.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DefaultPenalty = 6;
    }

    /// <summary>
    /// Gets or sets the default penalty value used for split alignment. 6 works well
    /// for most cases, higher values mean fewer splits.
    /// </summary>
    public int DefaultPenalty { get; set; }

    /// <summary>
    /// Gets or sets a custom path to the LAPSE engine binary. When empty, the plugin
    /// looks for it in the default download location instead.
    /// </summary>
    public string? EngineBinaryPathOverride { get; set; }

    /// <summary>
    /// Gets the sync history for every movie we've ever synced.
    /// </summary>
    public List<MovieSyncRecord> MovieRecords { get; } = new();

    /// <summary>
    /// Gets the ids of movies and folders that are marked as skip. A movie counts
    /// as skipped if its own id is here, or any of its parent folders' ids are.
    /// </summary>
    public List<Guid> SkippedItemIds { get; } = new();
}
