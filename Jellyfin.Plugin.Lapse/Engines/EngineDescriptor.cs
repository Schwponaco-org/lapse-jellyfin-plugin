// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// How an engine's release asset is packaged.
/// </summary>
public enum EnginePackaging
{
    /// <summary>
    /// The download is the executable itself, just needs the exec bit setting.
    /// </summary>
    RawBinary,

    /// <summary>
    /// The download is a .tar.gz that has the executable inside it.
    /// </summary>
    TarGz
}

/// <summary>
/// Everything static we know about one engine: what to call it, what it can do, and
/// where to get a build of it for this machine.
/// </summary>
public class EngineDescriptor
{
    /// <summary>
    /// Gets or sets the short id used in the API and config, e.g. "lapse".
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name shown in the dashboard.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a one line description for the engine card.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project's home page.
    /// </summary>
    public string ProjectUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what this engine can do.
    /// </summary>
    public EngineCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// Gets or sets the name of the executable once it's installed.
    /// </summary>
    public string ExecutableName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the download is packaged.
    /// </summary>
    public EnginePackaging Packaging { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine rewrites the subtitle it is
    /// pointed at instead of taking a separate output path. LAPSE works this way, alass
    /// and ffsubsync both take an explicit output argument. The runner needs to know so
    /// it can seed the work file for in-place engines, and so that "did the engine
    /// actually write anything" stays a meaningful check for the ones that don't.
    /// </summary>
    public bool EditsInPlace { get; set; }

    /// <summary>
    /// Gets or sets the download URL for 64 bit Intel/AMD, or null if there isn't a build.
    /// </summary>
    public string? Amd64Url { get; set; }

    /// <summary>
    /// Gets or sets the download URL for 64 bit ARM, or null if there isn't a build.
    /// alass for example only publishes an x86_64 binary.
    /// </summary>
    public string? Arm64Url { get; set; }
}
