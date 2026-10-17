// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Reads language and forced flag off a subtitle file name the same way Jellyfin does:
/// take what's after the video name, split on dots, walk from the end. forced/foreign,
/// sdh/cc/hi and default are flags, the last recognised language wins, the rest is title.
/// </summary>
public static class ExistingSubtitleNames
{
    private static readonly string[] ForcedFlags = { "forced", "foreign" };
    private static readonly string[] OtherFlags = { "sdh", "cc", "hi", "default" };

    /// <summary>
    /// Says whether a subtitle is named after the video (what Jellyfin needs to pair them).
    /// </summary>
    public static bool IsNamedAfter(string videoPath, string subtitlePath)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var name = Path.GetFileName(subtitlePath);

        return name.Length > stem.Length
            && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && name[stem.Length] == '.';
    }

    /// <summary>
    /// Reads a subtitle file's name.
    /// </summary>
    public static (string? Language, bool Forced) Parse(string videoPath, string subtitlePath, Func<string, bool> isLanguage)
    {
        if (!IsNamedAfter(videoPath, subtitlePath))
        {
            return (null, false);
        }

        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var rest = name.Length > stem.Length ? name[stem.Length..] : string.Empty;

        string? language = null;
        var forced = false;
        var sawHi = false;

        var pieces = rest.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = pieces.Length - 1; i >= 0; i--)
        {
            var piece = pieces[i];

            if (Array.Exists(ForcedFlags, f => f.Equals(piece, StringComparison.OrdinalIgnoreCase)))
            {
                forced = true;
                continue;
            }

            if (piece.Equals("hi", StringComparison.OrdinalIgnoreCase))
            {
                sawHi = true;
                continue;
            }

            if (Array.Exists(OtherFlags, f => f.Equals(piece, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (language is null && isLanguage(piece))
            {
                language = piece;
            }
        }

        // "hi" is a flag if there's another language in the name, otherwise Hindi
        if (language is null && sawHi)
        {
            language = "hi";
        }

        return (language, forced);
    }
}
