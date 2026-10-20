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
public sealed record SubtitleLanguageKey(string Code, string Base)
{
    /// <summary>
    /// Gets the key for a track with no language tag.
    /// </summary>
    public static SubtitleLanguageKey Undetermined { get; } = new("und", "und");

    /// <summary>
    /// Gets a value indicating whether this is a regional variant like pt-BR.
    /// </summary>
    public bool IsVariant => !string.Equals(Code, Base, StringComparison.Ordinal);
}

/// <summary>
/// Works out whether two ways of writing a language mean the same one ("en", "eng",
/// "English", "ger" vs "deu"...). Goes through Jellyfin's own language table so we agree
/// with what the player shows. "pt" matches pt-BR tracks, but "pt-BR" doesn't pull in pt-PT.
/// </summary>
public class SubtitleLanguages
{
    private static readonly char[] ListSeparators = { ',', ';', '|', ' ', '\t', '\r', '\n' };

    private readonly ILocalizationManager _localization;
    private readonly ConcurrentDictionary<string, SubtitleLanguageKey> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleLanguages"/> class.
    /// </summary>
    public SubtitleLanguages(ILocalizationManager localization)
    {
        _localization = localization;
    }

    /// <summary>
    /// Splits a typed list of languages on commas, semicolons, pipes or spaces.
    /// </summary>
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
    /// Says whether two tags are exactly the same language. Stricter than <see cref="Matches"/>
    /// on purpose, a pt-BR file shouldn't make us skip the pt-PT track. Worst case is a spare file.
    /// </summary>
    public static bool SameLanguage(SubtitleLanguageKey a, SubtitleLanguageKey b)
    {
        return string.Equals(a.Code, b.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a language tag or name.
    /// </summary>
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
    /// Says whether Jellyfin knows this as a language. Used to find the language in a file name.
    /// </summary>
    public bool IsLanguage(string? token)
    {
        return !string.IsNullOrWhiteSpace(token) && Find(token.Trim()) is not null;
    }

    /// <summary>
    /// Turns "en", "eng", "English", "pt_BR" etc into OpenSubtitles' code: two letters,
    /// except Portuguese and Chinese which they split by region.
    /// </summary>
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

        // not in Jellyfin's table but looks like a code, trust it
        return normalized.Length == 2 && char.IsAsciiLetter(normalized[0]) && char.IsAsciiLetter(normalized[1])
            ? normalized
            : null;
    }

    private SubtitleLanguageKey ResolveUncached(string tag)
    {
        // pt_BR is as common as pt-BR
        var normalized = tag.Replace('_', '-').ToLowerInvariant();

        var culture = Find(normalized);
        if (culture is not null)
        {
            var code = CodeOf(culture, normalized);

            // regional entries like pt-br carry the region in the two letter name, base is the plain language
            var two = culture.TwoLetterISOLanguageName;
            var dash = string.IsNullOrEmpty(two) ? -1 : two.IndexOf('-', StringComparison.Ordinal);

            if (dash > 0)
            {
                var baseCulture = Find(two![..dash]);
                return new SubtitleLanguageKey(code, baseCulture is null ? two[..dash].ToLowerInvariant() : CodeOf(baseCulture, two[..dash]));
            }

            return new SubtitleLanguageKey(code, code);
        }

        // en-US or zh-Hant aren't in the table, but the part before the dash is
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
            // throwing on an odd tag is the same as not finding it
            return null;
        }
    }
}
