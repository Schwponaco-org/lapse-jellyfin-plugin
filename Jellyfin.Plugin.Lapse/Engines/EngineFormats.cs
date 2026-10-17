// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Which subtitle formats each engine reads and writes.
///
/// LAPSE 2.0.3+ reads and writes a lot more than srt/vtt/ass/ssa (PGS and VobSub too,
/// rewriting timing only), so converting first is optional for it.
///
/// These lists are only the fallback, an installed binary gets asked with --formats
/// (see <see cref="EngineRuntimeInfo.GetSubtitleExtensions"/>).
/// </summary>
public static class EngineFormats
{
    /// <summary>
    /// What LAPSE 2.2.4 reads (subtitle_formats in its main.cpp), written back in the same
    /// format. .sub can be MicroDVD, MPL2 or SubViewer 2, the engine sniffs which. .idx is
    /// VobSub. It also sniffs .txt but that's left off on purpose, .txt in a media folder
    /// is rarely a subtitle (see <see cref="Services.SubtitleFormats.ConvertibleExtensions"/>).
    /// </summary>
    public static readonly string[] Lapse =
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".mpl2", ".sup", ".sbv", ".idx", ".smi", ".sami", ".ttml", ".dfxp"
    };

    /// <summary>
    /// What alass reads. Its README also lists idx and sub, but we only claim the text ones,
    /// overclaiming means handing it files it fails on.
    /// </summary>
    public static readonly string[] Alass = { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>
    /// What ffsubsync reads.
    /// </summary>
    public static readonly string[] Ffsubsync = { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>
    /// Gets the formats an engine reads, without asking its binary.
    /// </summary>
    public static IReadOnlyList<string> ForEngine(string? engineId)
    {
        return (engineId ?? string.Empty).ToLowerInvariant() switch
        {
            "lapse" => Lapse,
            "alass" => Alass,
            "ffsubsync" => Ffsubsync,

            // unknown engine, the safe set
            _ => Ffsubsync
        };
    }

    /// <summary>
    /// Says whether an engine reads a file, from the static list (the runner uses the probed answer instead).
    /// </summary>
    public static bool CanRead(string? engineId, string? path)
    {
        return Contains(ForEngine(engineId), path);
    }

    /// <summary>
    /// Says whether the engine a plain Sync press would use reads a file as it stands.
    /// </summary>
    public static bool DefaultEngineCanRead(string? path)
    {
        return CanRead(Plugin.Instance?.Configuration.DefaultEngineId, path);
    }

    /// <summary>
    /// Checks a path against a list of extensions.
    /// </summary>
    public static bool Contains(IReadOnlyList<string> extensions, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < extensions.Count; i++)
        {
            if (string.Equals(extensions[i], extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a list of extensions into something to show a person: ".srt, .ass, .vtt".
    /// </summary>
    public static string Describe(IReadOnlyList<string> extensions)
    {
        return string.Join(", ", extensions);
    }
}
