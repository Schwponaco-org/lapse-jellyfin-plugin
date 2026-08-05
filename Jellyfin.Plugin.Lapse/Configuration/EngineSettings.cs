// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

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
}
