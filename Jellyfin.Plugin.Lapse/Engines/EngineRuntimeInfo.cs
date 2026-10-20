// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// What the installed binary actually supports, as opposed to
/// <see cref="EngineCapabilities"/> which is what we statically know about the project.
/// Two installs can have very different builds, so we ask instead of assuming.
/// </summary>
public class EngineRuntimeInfo
{
    /// <summary>
    /// Gets the info for a binary we couldn't probe. Assumes no optional flags.
    /// </summary>
    public static EngineRuntimeInfo Unknown { get; } = new();

    /// <summary>
    /// Gets or sets the version the engine reported, if it reports one at all.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets the command line flags the binary said it understands.
    /// </summary>
    public List<string> Flags { get; } = new();

    /// <summary>
    /// Gets the extensions from --formats. Empty if it has no such call (then <see cref="EngineFormats.ForEngine"/> is used).
    /// </summary>
    public List<string> ReportedExtensions { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the probe got an answer at all.
    /// </summary>
    public bool Probed { get; set; }

    /// <summary>
    /// Gets or sets where the flags came from: "capabilities", "usage" or "version".
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets a value indicating whether the binary takes --output.
    /// </summary>
    public bool SupportsOutputFlag => HasFlag("--output");

    /// <summary>
    /// Gets a value indicating whether the binary takes --no-backup.
    /// </summary>
    public bool SupportsNoBackupFlag => HasFlag("--no-backup");

    /// <summary>
    /// Gets a value indicating whether it takes --batch (LAPSE 2.2.3+).
    /// </summary>
    public bool SupportsBatch => HasFlag("--batch");

    /// <summary>
    /// Gets or sets the usage text (trimmed). Kept because auto mode is positional, the
    /// only way to tell if a build has it is the mode list in the usage text.
    /// </summary>
    public string? UsageText { get; set; }

    /// <summary>
    /// Gets a value indicating whether it has auto mode. Older builds reject "auto" outright.
    /// </summary>
    public bool SupportsAutoMode =>
        UsageText is not null && UsageText.Contains("auto|", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets what this engine reads: what the binary said, else the built-in list.
    /// </summary>
    public IReadOnlyList<string> GetSubtitleExtensions(string engineId)
    {
        return ReportedExtensions.Count > 0 ? ReportedExtensions : EngineFormats.ForEngine(engineId);
    }

    /// <summary>
    /// Says whether this binary reads a file as is, without converting.
    /// </summary>
    public bool CanRead(string engineId, string? path)
    {
        return EngineFormats.Contains(GetSubtitleExtensions(engineId), path);
    }

    /// <summary>
    /// Checks whether the binary said it understands a flag.
    /// </summary>
    public bool HasFlag(string flag)
    {
        return Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
    }
}
