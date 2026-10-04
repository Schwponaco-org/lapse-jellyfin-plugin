// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// A language, boiled down to something two tags can be compared on.
/// </summary>
/// <param name="Code">The language itself: Jellyfin's three letter code when it knows the
/// language, the tag in lower case when it doesn't, "und" for no tag at all.</param>
/// <param name="Base">The language without its region or script. The same as
/// <paramref name="Code"/> for anything that doesn't have one.</param>
public sealed record SubtitleLanguageKey(string Code, string Base)
{
    /// <summary>
    /// Gets the key for a track with no language tag.
    /// </summary>
    public static SubtitleLanguageKey Undetermined { get; } = new("und", "und");

    /// <summary>
    /// Gets a value indicating whether this names a regional variant, like pt-BR, rather
    /// than a whole language.
    /// </summary>
    public bool IsVariant => !string.Equals(Code, Base, StringComparison.Ordinal);
}

/// <summary>
/// Works out whether two ways of writing a language mean the same one.
///
/// Subtitle tracks are tagged every way there is: "en", "eng", "English", "pt-BR",
/// bibliographic "ger" next to terminological "deu", or nothing at all. People typing a
/// list of languages into a settings box are no more consistent. Everything goes through
/// Jellyfin's own language table, the same one it uses to name tracks in the player, so
/// a subtitle LAPSE thinks is English is one Jellyfin will call English too.
///
/// A plain language matches its regional variants - "pt" takes pt-BR tracks as well - but
/// not the other way round, so asking for "pt-BR" alone doesn't pull in European
/// Portuguese.
/// </summary>
public class SubtitleLanguages
{
    private static readonly char[] ListSeparators = { ',', ';', '|', ' ', '\t', '\r', '\n' };

    private readonly ILocalizationManager _localization;
    private readonly ConcurrentDictionary<string, SubtitleLanguageKey> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleLanguages"/> class.
    /// </summary>
    /// <param name="localization">Jellyfin's language table.</param>
    public SubtitleLanguages(ILocalizationManager localization)
    {
        _localization = localization;
    }

    /// <summary>
    /// Splits a typed list of languages. Commas, semicolons, pipes and spaces all work,
    /// since all of them get used.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <returns>The entries, trimmed, duplicates and blanks gone.</returns>
    public static List<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Says whether a track's language is one of the wanted ones.
    /// </summary>
    /// <param name="wanted">The wanted languages, already resolved.</param>
    /// <param name="track">The track's language.</param>
    /// <returns>True on a match.</returns>
    public static bool Matches(IEnumerable<SubtitleLanguageKey> wanted, SubtitleLanguageKey track)
    {
        foreach (var language in wanted)
        {
            if (string.Equals(language.Code, track.Code, StringComparison.Ordinal))
            {
                return true;
            }

            if (!language.IsVariant && string.Equals(language.Base, track.Base, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Says whether two tags name exactly the same language. Stricter than
    /// <see cref="Matches"/> on purpose: this decides whether a subtitle already on disk
    /// makes a track redundant, and a Brazilian Portuguese file is no reason to leave the
    /// European Portuguese track in the video. Erring towards "different" costs a spare
    /// file at worst, never a missing one.
    /// </summary>
    /// <param name="a">One key.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they're the same language.</returns>
    public static bool SameLanguage(SubtitleLanguageKey a, SubtitleLanguageKey b)
    {
        return string.Equals(a.Code, b.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a language tag or name.
    /// </summary>
    /// <param name="tag">The tag, name or code. Null or blank means no language.</param>
    /// <returns>The key to compare on.</returns>
    public SubtitleLanguageKey Resolve(string? tag)
    {
        var trimmed = tag?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return SubtitleLanguageKey.Undetermined;
        }

        return _cache.GetOrAdd(trimmed, ResolveUncached);
    }

    /// <summary>
    /// Says whether Jellyfin knows a string as a language at all. Used to pick the
    /// language out of the pieces of a subtitle's file name.
    /// </summary>
    /// <param name="token">A piece of a file name.</param>
    /// <returns>True when it names a language.</returns>
    public bool IsLanguage(string? token)
    {
        return !string.IsNullOrWhiteSpace(token) && Find(token.Trim()) is not null;
    }

    /// <summary>
    /// Turns a language however it was typed - "en", "eng", "English", "pt_BR" - into the
    /// code OpenSubtitles files subtitles under. That's the two letter code, except for
    /// the few languages it splits by region: Portuguese and Chinese.
    /// </summary>
    /// <param name="tag">The language as typed.</param>
    /// <returns>The OpenSubtitles code, or null when it isn't a language.</returns>
    public string? ToOpenSubtitlesCode(string? tag)
    {
        var normalized = tag?.Trim().Replace('_', '-').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        switch (normalized)
        {
            case "pt" or "pt-pt" or "por" or "portuguese":
                return "pt-pt";
            case "pt-br" or "pob" or "brazilian portuguese" or "portuguese (brazil)":
                return "pt-br";
            case "zh" or "zh-cn" or "zh-hans" or "chi" or "zho" or "chinese":
                return "zh-cn";
            case "zh-tw" or "zh-hant" or "zh-hk":
                return "zh-tw";
            default:
                break;
        }

        var culture = Find(normalized);
        var two = culture?.TwoLetterISOLanguageName?.ToLowerInvariant();

        if (!string.IsNullOrEmpty(two))
        {
            return two == "pt" ? "pt-pt" : two;
        }

        // Not in Jellyfin's table, but already shaped like a code: trust it.
        return normalized.Length == 2 && char.IsAsciiLetter(normalized[0]) && char.IsAsciiLetter(normalized[1])
            ? normalized
            : null;
    }

    private SubtitleLanguageKey ResolveUncached(string tag)
    {
        // pt_BR turns up as often as pt-BR.
        var normalized = tag.Replace('_', '-').ToLowerInvariant();

        var culture = Find(normalized);
        if (culture is not null)
        {
            var code = CodeOf(culture, normalized);

            // Jellyfin's table has regional entries of its own, pt-br among them, whose
            // two letter name carries the region. Their base is the plain language.
            var two = culture.TwoLetterISOLanguageName;
            var dash = string.IsNullOrEmpty(two) ? -1 : two.IndexOf('-', StringComparison.Ordinal);

            if (dash > 0)
            {
                var baseCulture = Find(two![..dash]);
                return new SubtitleLanguageKey(code, baseCulture is null ? two[..dash].ToLowerInvariant() : CodeOf(baseCulture, two[..dash]));
            }

            return new SubtitleLanguageKey(code, code);
        }

        // Not in the table as written. A tag like en-US or zh-Hant still has a language
        // in front of the region that the table does know.
        var separator = normalized.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0)
        {
            var prefix = normalized[..separator];
            var prefixCulture = Find(prefix);

            return new SubtitleLanguageKey(normalized, prefixCulture is null ? prefix : CodeOf(prefixCulture, prefix));
        }

        return new SubtitleLanguageKey(normalized, normalized);
    }

    private static string CodeOf(CultureDto culture, string fallback)
    {
        var code = culture.ThreeLetterISOLanguageName;
        return string.IsNullOrWhiteSpace(code) ? fallback.ToLowerInvariant() : code.ToLowerInvariant();
    }

    private CultureDto? Find(string value)
    {
        try
        {
            return _localization.FindLanguageInfo(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // A lookup that throws on some odd tag means the same as one that finds
            // nothing: go by the tag as written.
            return null;
        }
    }
}
