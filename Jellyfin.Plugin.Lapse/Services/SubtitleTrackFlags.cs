// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Reads flags out of a track title, since lots of releases write "English (Forced)" or
/// "English SDH" instead of setting the container flag. Watch out for "Non-Forced" and
/// friends, common titles for the full track next to a forced one.
/// </summary>
public static class SubtitleTrackFlags
{
    private static readonly string[] ForcedWords = { "forced" };

    private static readonly string[] HearingImpairedWords =
    {
        "sdh", "cc", "hearing impaired", "hard of hearing", "closed caption", "closed captions"
    };

    private static readonly string[] Negations = { "non", "not", "no", "without" };

    /// <summary>
    /// Says whether a track title calls the track forced.
    /// </summary>
    public static bool TitleSaysForced(string? title)
    {
        return ContainsUnnegated(title, ForcedWords);
    }

    /// <summary>
    /// Says whether a track title calls the track SDH or closed captions.
    /// </summary>
    public static bool TitleSaysHearingImpaired(string? title)
    {
        return ContainsUnnegated(title, HearingImpairedWords);
    }

    private static bool ContainsUnnegated(string? title, string[] words)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        foreach (var word in words)
        {
            var start = 0;

            while (start < title.Length)
            {
                var at = title.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                var end = at + word.Length;

                // whole words only, "Unforced" shouldn't match
                if (IsBoundary(title, at - 1) && IsBoundary(title, end) && !IsNegated(title, at))
                {
                    return true;
                }

                start = at + 1;
            }
        }

        return false;
    }

    private static bool IsBoundary(string text, int index)
    {
        return index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);
    }

    // "Non-Forced", "(no forced)", "without SDH": check the word right before, skipping separators
    private static bool IsNegated(string title, int matchStart)
    {
        var end = matchStart;
        while (end > 0 && !char.IsLetterOrDigit(title[end - 1]))
        {
            end--;
        }

        var begin = end;
        while (begin > 0 && char.IsLetterOrDigit(title[begin - 1]))
        {
            begin--;
        }

        if (begin == end)
        {
            return false;
        }

        var previous = title.AsSpan(begin, end - begin);

        foreach (var negation in Negations)
        {
            if (previous.Equals(negation, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
