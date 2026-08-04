// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Whether the LAPSE engine binary is ready to use, for the dashboard's download section.
/// </summary>
public class EngineStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a usable engine binary was found.
    /// </summary>
    public bool Downloaded { get; set; }

    /// <summary>
    /// Gets or sets the path the plugin resolved (whether or not it actually exists yet).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the detected OS description, e.g. "linux-x64".
    /// </summary>
    public string OsArch { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this OS/arch has a published binary at all.
    /// Only Linux amd64/arm64 builds are published, everything else needs the path override.
    /// </summary>
    public bool DownloadSupported { get; set; }

    /// <summary>
    /// Gets or sets why the engine can't actually run, if it can't. Null means either it
    /// wasn't downloaded at all (see <see cref="Downloaded"/>) or it looks fine. A file
    /// existing on disk doesn't mean it can actually start - most commonly a missing
    /// shared library on whatever system it's running on.
    /// </summary>
    public string? RunCheckError { get; set; }
}
