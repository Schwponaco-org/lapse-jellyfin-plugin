// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    TarGz,

    /// <summary>
    /// The download is a .zip that has the executable inside it. This is how the Windows
    /// builds of alass and ffsubsync are published.
    /// </summary>
    Zip
}

/// <summary>
/// One downloadable build of an engine: where it is and how it's wrapped.
/// </summary>
public class EngineDownload
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EngineDownload"/> class.
    /// </summary>
    /// <param name="url">Where to get it.</param>
    /// <param name="packaging">How it's wrapped.</param>
    public EngineDownload(string url, EnginePackaging packaging)
    {
        Url = url;
        Packaging = packaging;
    }

    /// <summary>
    /// Gets the download URL.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets how the download is packaged.
    /// </summary>
    public EnginePackaging Packaging { get; }

    /// <summary>
    /// Gets the extra files published alongside the main executable that should sit next
    /// to it once installed - a shared library or a model file the binary looks for beside
    /// itself, say. Optional on purpose: an engine whose binary works fine without one of
    /// these (falls back to something built in, for example) shouldn't fail an install just
    /// because GitHub renamed or dropped the sidecar asset.
    /// </summary>
    public List<EngineSidecarAsset> Sidecars { get; } = new();
}

/// <summary>
/// A file published next to an engine's main download that isn't the executable itself,
/// but needs to sit beside it once installed.
/// </summary>
public class EngineSidecarAsset
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EngineSidecarAsset"/> class.
    /// </summary>
    /// <param name="url">Where to get it.</param>
    /// <param name="fileName">What to name it once it's sitting next to the executable.</param>
    public EngineSidecarAsset(string url, string fileName)
    {
        Url = url;
        FileName = fileName;
    }

    /// <summary>
    /// Gets the download URL.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets the file name to save it under, in the engine's own install folder.
    /// </summary>
    public string FileName { get; }
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
    /// Gets or sets the "owner/name" of the GitHub repo releases come from, used for the
    /// update check. Null means the plugin can't tell what version is out there.
    /// </summary>
    public string? GitHubRepo { get; set; }

    /// <summary>
    /// Gets or sets a page explaining how to build this engine from source, shown when
    /// there's no download for the machine the server is running on.
    /// </summary>
    public string? BuildGuideUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this engine is still rough enough that
    /// people should know before they lean on it. The dashboard puts (EXPERIMENTAL) after
    /// the name when this is set.
    /// </summary>
    public bool Experimental { get; set; }

    /// <summary>
    /// Gets or sets what this engine can do.
    /// </summary>
    public EngineCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// Gets or sets the name of the executable once it's installed, without any platform
    /// extension - use <see cref="GetExecutableFileName"/> to get the real file name.
    /// </summary>
    public string ExecutableName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the engine rewrites the subtitle it is
    /// pointed at instead of taking a separate output path. LAPSE works this way, alass
    /// and ffsubsync both take an explicit output argument. The runner needs to know so
    /// it can seed the work file for in-place engines, and so that "did the engine
    /// actually write anything" stays a meaningful check for the ones that don't.
    /// </summary>
    public bool EditsInPlace { get; set; }

    /// <summary>
    /// Gets or sets the Linux build for 64 bit Intel/AMD, or null if there isn't one.
    /// </summary>
    public EngineDownload? LinuxAmd64 { get; set; }

    /// <summary>
    /// Gets or sets the Linux build for 64 bit ARM, or null if there isn't one.
    /// </summary>
    public EngineDownload? LinuxArm64 { get; set; }

    /// <summary>
    /// Gets or sets the Windows build for 64 bit Intel/AMD, or null if there isn't one.
    /// </summary>
    public EngineDownload? WindowsAmd64 { get; set; }

    /// <summary>
    /// Gets or sets the macOS build for 64 bit Intel, or null if there isn't one.
    /// </summary>
    public EngineDownload? MacAmd64 { get; set; }

    /// <summary>
    /// Gets or sets the macOS build for Apple silicon, or null if there isn't one.
    /// </summary>
    public EngineDownload? MacArm64 { get; set; }

    /// <summary>
    /// Gets the file name the executable has on this platform, which is the same as
    /// <see cref="ExecutableName"/> everywhere except Windows.
    /// </summary>
    /// <returns>The executable file name.</returns>
    public string GetExecutableFileName()
    {
        return OperatingSystem.IsWindows() ? ExecutableName + ".exe" : ExecutableName;
    }

    /// <summary>
    /// Gets the build to download for the OS and CPU this server is running on, or null
    /// if the project doesn't publish one.
    /// </summary>
    /// <returns>The download, or null.</returns>
    public EngineDownload? GetDownloadForThisMachine()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;

        if (OperatingSystem.IsWindows())
        {
            // Nobody here ships a Windows ARM build, and x64 runs fine under emulation on
            // the machines that would want one, so both architectures get the x64 asset.
            return WindowsAmd64;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? MacArm64 ?? MacAmd64 : MacAmd64;
        }

        return arm ? LinuxArm64 : LinuxAmd64;
    }
}
