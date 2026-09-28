// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Reads the language and forced flag off a subtitle file's name, the way Jellyfin does.
///
/// Jellyfin takes the part of the name after the video's own, splits it on dots, and
/// walks the pieces from the end: "forced" or "foreign" marks it forced, "sdh", "cc" and
/// "hi" mark it for the hard of hearing, "default" marks it default, and the last piece
/// it recognises as a language is the language. Everything else is title. Doing the same
/// here means a file LAPSE counts as "English, forced" is one Jellyfin shows as that, and
/// a file Jellyfin can't tell the language of isn't counted as covering any language.
/// </summary>
public static class ExistingSubtitleNames
{
    private static readonly string[] ForcedFlags = { "forced", "foreign" };
    private static readonly string[] OtherFlags = { "sdh", "cc", "hi", "default" };

    /// <summary>
    /// Says whether a subtitle file is named after the video, which is what Jellyfin
    /// needs to put it with that video.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="subtitlePath">The subtitle.</param>
    /// <returns>True when the name starts with the video's name and a dot.</returns>
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
    /// <param name="videoPath">The video the subtitle goes with.</param>
    /// <param name="subtitlePath">The subtitle.</param>
    /// <param name="isLanguage">Says whether a piece of the name is a language.</param>
    /// <returns>The language as written in the name (or null), and whether it's forced.</returns>
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

        // "hi" is both Hindi and the hard of hearing flag. Jellyfin reads it as the flag
        // when the name has another language in it, and as Hindi when it doesn't.
        if (language is null && sawHi)
        {
            language = "hi";
        }

        return (language, forced);
    }
}
