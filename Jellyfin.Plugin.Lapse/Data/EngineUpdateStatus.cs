// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Where one engine stands against its published releases.
/// </summary>
public class EngineUpdateStatus
{
    /// <summary>
    /// Gets or sets the engine id.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release tag currently installed, or null when the engine wasn't
    /// installed through the plugin so there's nothing recorded.
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets the newest release tag published, or null if GitHub couldn't be asked.
    /// </summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there's something newer to install.
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the daily task may update this engine.
    /// </summary>
    public bool AutoUpdate { get; set; }

    /// <summary>
    /// Gets or sets when the plugin last asked GitHub about this engine.
    /// </summary>
    public DateTime? LastCheckedUtc { get; set; }
}
