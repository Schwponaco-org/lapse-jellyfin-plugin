// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Reads a subtitle track's title for what the release meant to flag but didn't.
///
/// Jellyfin only goes by the container's flags, and plenty of releases leave those unset
/// and write "English (Forced)" or "English SDH" in the title instead. Going by the title
/// as well is what makes a forced-only extraction find those tracks at all, and what gets
/// the forced flag onto the extracted file where Jellyfin will see it.
///
/// The one trap in reading titles is the negative: "Non-Forced" and "no forced" contain
/// the word but mean the opposite, and those are common titles for the full track that
/// sits next to a forced one.
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
    /// <param name="title">The title, or null.</param>
    /// <returns>True when the title says forced and doesn't negate it.</returns>
    public static bool TitleSaysForced(string? title)
    {
        return ContainsUnnegated(title, ForcedWords);
    }

    /// <summary>
    /// Says whether a track title calls the track SDH or closed captions.
    /// </summary>
    /// <param name="title">The title, or null.</param>
    /// <returns>True when the title says so and doesn't negate it.</returns>
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

                // Whole words only, so "Unforced" or "accessibility" can't match on a
                // fragment.
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

    // "Non-Forced", "non forced", "(no forced)", "not forced", "without SDH". Looks at the
    // word right before the match, skipping the separators people put in between.
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
