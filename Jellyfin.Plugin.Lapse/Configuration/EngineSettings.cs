// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// Per-engine settings. Stored as a list rather than a dictionary because Jellyfin's XML
/// config serializer handles lists cleanly and dictionaries badly.
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Gets or sets which engine this is for.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom path to the binary. When empty the plugin uses whatever it
    /// installed into its own engines folder.
    /// </summary>
    public string? PathOverride { get; set; }

    /// <summary>
    /// Gets or sets the penalty to use for split mode with this engine. Null means use the
    /// engine's own default, which matters because the scales differ wildly between them.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets the release tag of the copy the plugin installed, e.g. "v1.0.7". Null
    /// when the engine was never installed through the plugin (a hand built binary behind
    /// a path override, say), in which case there's nothing to compare a release against.
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the daily task may replace this engine's
    /// binary when a newer release shows up.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Gets or sets when the plugin last asked GitHub what the newest release was. Used to
    /// keep the dashboard from hammering the API on every page load.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Gets or sets the newest release tag seen at <see cref="LastUpdateCheckUtc"/>.
    /// </summary>
    public string? LatestKnownVersion { get; set; }
}
