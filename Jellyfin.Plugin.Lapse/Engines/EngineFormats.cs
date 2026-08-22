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
/// This used to be one list for all of them, because it used to be true: every engine
/// took srt, vtt, ass or ssa and nothing else, so anything else had to be converted
/// first. LAPSE 2.0.3 reads and writes nine more, including the two picture based ones,
/// where it rewrites the timing without touching the bitmaps. Converting before syncing
/// is therefore no longer something the plugin has to do, only something it can do.
///
/// The lists here are the fallback. When a binary is installed the plugin asks it with
/// <c>--formats</c> and uses that answer instead, so a newer or older build than the one
/// this was written against is still handled correctly. See
/// <see cref="EngineRuntimeInfo.GetSubtitleExtensions"/>.
/// </summary>
public static class EngineFormats
{
    /// <summary>
    /// What LAPSE 2.0.3 reads, taken from the subtitle_formats array in its main.cpp. It
    /// writes back in whatever format it read, so nothing on this list needs converting
    /// on the way in or on the way out.
    /// </summary>
    public static readonly string[] Lapse =
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup", ".sbv", ".idx", ".smi", ".ttml", ".dfxp"
    };

    /// <summary>
    /// What alass reads. Its own README lists srt, ass, ssa, idx and sub, but the plugin
    /// only claims the plain text ones here: an engine that turns out to read more says so
    /// through --formats, and claiming too much would mean handing it a file it fails on.
    /// </summary>
    public static readonly string[] Alass = { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>
    /// What ffsubsync reads.
    /// </summary>
    public static readonly string[] Ffsubsync = { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>
    /// Gets the formats an engine reads, without asking its binary.
    /// </summary>
    /// <param name="engineId">The engine id.</param>
    /// <returns>The extensions, each with a leading dot.</returns>
    public static IReadOnlyList<string> ForEngine(string? engineId)
    {
        return (engineId ?? string.Empty).ToLowerInvariant() switch
        {
            "lapse" => Lapse,
            "alass" => Alass,
            "ffsubsync" => Ffsubsync,

            // An engine the plugin doesn't know gets the set every one of them has always
            // taken, which is the safe answer rather than the generous one.
            _ => Ffsubsync
        };
    }

    /// <summary>
    /// Says whether an engine reads a file as it stands, going by the static list rather
    /// than by what the installed binary reports. Used where there's no binary to ask -
    /// listing an item's subtitles, mostly - while the runner uses the probed answer.
    /// </summary>
    /// <param name="engineId">The engine id.</param>
    /// <param name="path">The subtitle path.</param>
    /// <returns>True if that engine takes the file directly.</returns>
    public static bool CanRead(string? engineId, string? path)
    {
        return Contains(ForEngine(engineId), path);
    }

    /// <summary>
    /// Says whether the engine a plain Sync press would use reads a file as it stands.
    /// </summary>
    /// <param name="path">The subtitle path.</param>
    /// <returns>True if the default engine takes the file directly.</returns>
    public static bool DefaultEngineCanRead(string? path)
    {
        return CanRead(Plugin.Instance?.Configuration.DefaultEngineId, path);
    }

    /// <summary>
    /// Checks a path against a list of extensions.
    /// </summary>
    /// <param name="extensions">The extensions to match, each with a leading dot.</param>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the path's extension is on the list.</returns>
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
    /// <param name="extensions">The extensions.</param>
    /// <returns>A comma separated list.</returns>
    public static string Describe(IReadOnlyList<string> extensions)
    {
        return string.Join(", ", extensions);
    }
}
