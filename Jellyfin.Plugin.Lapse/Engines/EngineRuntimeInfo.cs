// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// What the binary that's actually installed turned out to support, as opposed to
/// <see cref="EngineCapabilities"/> which is what we statically know about the project.
/// Two people running the same plugin can have very different builds of an engine on
/// disk, so anything version dependent gets asked of the binary rather than assumed.
/// </summary>
public class EngineRuntimeInfo
{
    /// <summary>
    /// Gets the runtime info to use when the binary couldn't be probed at all. Nothing
    /// optional is assumed to be there, which is the conservative fallback the plugin
    /// wants for an engine it can't ask.
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
    /// Gets the subtitle extensions the binary said it reads, from its <c>--formats</c>
    /// call. Empty when the binary has no such call, in which case the caller falls back
    /// to <see cref="EngineFormats.ForEngine"/>.
    /// </summary>
    public List<string> ReportedExtensions { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the probe actually got an answer. False
    /// means everything else here is the conservative default rather than measured.
    /// </summary>
    public bool Probed { get; set; }

    /// <summary>
    /// Gets or sets how the flags were worked out, for the dashboard tooltip: either
    /// "capabilities" (the engine has a --capabilities call) or "usage" (we read them
    /// out of the usage text it prints).
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
    /// Gets or sets the usage text the binary printed, trimmed to something sane. Kept
    /// because not everything worth knowing is a flag: LAPSE's auto mode is a positional
    /// argument, so the only way to tell a build that has it from one that doesn't is to
    /// look at the mode list it prints.
    /// </summary>
    public string? UsageText { get; set; }

    /// <summary>
    /// Gets a value indicating whether the binary offers LAPSE's auto mode. Builds from
    /// before it existed reject "auto" as an unknown mode rather than ignoring it, so
    /// this has to be checked before asking for it.
    /// </summary>
    public bool SupportsAutoMode =>
        UsageText is not null && UsageText.Contains("auto|", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the subtitle formats this engine reads: what the binary reported if it
    /// reported anything, otherwise the list the plugin knows the project by.
    /// </summary>
    /// <param name="engineId">The engine id, for the fallback list.</param>
    /// <returns>The extensions, each with a leading dot.</returns>
    public IReadOnlyList<string> GetSubtitleExtensions(string engineId)
    {
        return ReportedExtensions.Count > 0 ? ReportedExtensions : EngineFormats.ForEngine(engineId);
    }

    /// <summary>
    /// Says whether this binary reads a subtitle file as it stands, so it can be handed
    /// over without being converted first.
    /// </summary>
    /// <param name="engineId">The engine id, for the fallback list.</param>
    /// <param name="path">The subtitle path.</param>
    /// <returns>True if the engine takes the file directly.</returns>
    public bool CanRead(string engineId, string? path)
    {
        return EngineFormats.Contains(GetSubtitleExtensions(engineId), path);
    }

    /// <summary>
    /// Checks whether the binary said it understands a flag.
    /// </summary>
    /// <param name="flag">The flag, including its leading dashes.</param>
    /// <returns>True if it's in the list.</returns>
    public bool HasFlag(string flag)
    {
        return Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
    }
}
