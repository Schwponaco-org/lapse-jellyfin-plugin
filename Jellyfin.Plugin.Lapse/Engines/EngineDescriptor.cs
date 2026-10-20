// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Lapse.Data;

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
    /// A .zip with the executable inside (alass and ffsubsync on Windows).
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
    /// Gets files in the archive that go next to the executable (LAPSE's onnxruntime and
    /// Silero model). Best effort, a missing one is skipped since the engine still runs.
    /// </summary>
    public List<string> CompanionFiles { get; } = new();
}

/// <summary>
/// How much we stand behind an engine, shown as a badge in the picker.
/// </summary>
public enum EngineTier
{
    /// <summary>
    /// The engine the plugin is built around and the one new installs get pointed at.
    /// </summary>
    Recommended,

    /// <summary>
    /// Third party, works and is kept wired up, but not what the plugin is built around.
    /// </summary>
    Supported,

    /// <summary>
    /// Rough enough that people should know before they lean on it.
    /// </summary>
    Experimental
}

/// <summary>
/// One alignment mode an engine offers. Per engine because they differ a lot between them.
/// </summary>
public class EngineModeOption
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EngineModeOption"/> class.
    /// </summary>
    public EngineModeOption(SyncMode mode, string label, string description)
    {
        Mode = mode;
        Label = label;
        Description = description;
    }

    /// <summary>
    /// Gets the mode.
    /// </summary>
    public SyncMode Mode { get; }

    /// <summary>
    /// Gets the label shown in the dropdown.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the one line explanation.
    /// </summary>
    public string Description { get; }
}

/// <summary>
/// Everything static about an engine: name, capabilities, downloads.
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
    /// Gets or sets the GitHub "owner/name" for update checks. Null = can't check.
    /// </summary>
    public string? GitHubRepo { get; set; }

    /// <summary>
    /// Gets or sets a build-from-source page, shown when there's no download for this machine.
    /// </summary>
    public string? BuildGuideUrl { get; set; }

    /// <summary>
    /// Gets or sets the tier badge.
    /// </summary>
    public EngineTier Tier { get; set; } = EngineTier.Supported;

    /// <summary>
    /// Gets or sets a page backing up the badge (LAPSE links its benchmarks).
    /// </summary>
    public string? WhyUrl { get; set; }

    /// <summary>
    /// Gets or sets the text of the <see cref="WhyUrl"/> link.
    /// </summary>
    public string? WhyLabel { get; set; }

    /// <summary>
    /// Gets or sets what this engine can do.
    /// </summary>
    public EngineCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// Gets the tunables the binary accepts, shown in the Advanced section.
    /// </summary>
    public List<EngineParameter> Parameters { get; } = new();

    /// <summary>
    /// Gets or sets a note at the top of the Advanced section, e.g. why something isn't there.
    /// </summary>
    public string? AdvancedNote { get; set; }

    /// <summary>
    /// Gets or sets a note about running the engine outside Jellyfin, shown on the card.
    /// </summary>
    public string? DeploymentNote { get; set; }

    /// <summary>
    /// Gets the modes in display order. The first is the default for a fresh install.
    /// </summary>
    public List<EngineModeOption> Modes { get; } = new();

    /// <summary>
    /// Gets or sets the executable name without extension, see <see cref="GetExecutableFileName"/>.
    /// </summary>
    public string ExecutableName { get; set; } = string.Empty;

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
    public string GetExecutableFileName()
    {
        return OperatingSystem.IsWindows() ? ExecutableName + ".exe" : ExecutableName;
    }

    /// <summary>
    /// Gets the build to download for the OS and CPU this server is running on, or null
    /// if the project doesn't publish one.
    /// </summary>
    public EngineDownload? GetDownloadForThisMachine()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;

        if (OperatingSystem.IsWindows())
        {
            // no Windows ARM builds anywhere, x64 runs fine under emulation
            return WindowsAmd64;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? MacArm64 ?? MacAmd64 : MacAmd64;
        }

        return arm ? LinuxArm64 : LinuxAmd64;
    }
}
