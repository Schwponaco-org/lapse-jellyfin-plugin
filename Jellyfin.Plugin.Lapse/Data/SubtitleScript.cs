// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// The writing system of a subtitle's dialogue. The readable preset is built for Latin and makes
/// Arabic or Thai harder to read if applied as is.
/// </summary>
public enum SubtitleScript
{
    /// <summary>
    /// Nothing to go on (empty, or no letters). Treated as Latin.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The Latin alphabet, with or without accents. What the readable preset is for.
    /// </summary>
    Latin = 1,

    /// <summary>
    /// Cyrillic: Russian, Ukrainian, Bulgarian, Serbian and the rest.
    /// </summary>
    Cyrillic = 2,

    /// <summary>
    /// Greek.
    /// </summary>
    Greek = 3,

    /// <summary>
    /// Arabic script (Arabic, Persian, Urdu, Pashto). RTL and joined letters, so no letter spacing.
    /// </summary>
    Arabic = 4,

    /// <summary>
    /// Hebrew. Right to left, but its letters stand apart, so spacing is harmless.
    /// </summary>
    Hebrew = 5,

    /// <summary>
    /// Thai, Lao and Khmer: no spaces between words, and marks that stack above and below
    /// the letters they belong to.
    /// </summary>
    Thai = 6,

    /// <summary>
    /// Indic scripts (Devanagari, Bengali, Tamil...). Letters form clusters that spacing breaks.
    /// </summary>
    Indic = 7,

    /// <summary>
    /// Chinese, Japanese and Korean. Every glyph is already its own square block, so the
    /// readable preset's tracking does nothing useful and its typeface has none of them.
    /// </summary>
    Cjk = 8
}

/// <summary>
/// Works out which writing system a subtitle is in, and what that means for the way it
/// should be styled.
/// </summary>
public static class SubtitleScripts
{
    // Unicode ranges per script, as escapes so editors don't reorder the RTL rows on screen.
    // Only what's needed to pick a font and a direction.
    private static readonly (char Start, char End, SubtitleScript Script)[] Ranges =
    {
        ('A', '\u024F', SubtitleScript.Latin),     // Latin, with its extensions
        ('\u1E00', '\u1EFF', SubtitleScript.Latin),     // additional, which is where Vietnamese lives
        ('\u0370', '\u03FF', SubtitleScript.Greek),
        ('\u1F00', '\u1FFF', SubtitleScript.Greek),     // polytonic
        ('\u0400', '\u052F', SubtitleScript.Cyrillic),
        ('\u0590', '\u05FF', SubtitleScript.Hebrew),
        ('\uFB1D', '\uFB4F', SubtitleScript.Hebrew),    // presentation forms
        ('\u0600', '\u06FF', SubtitleScript.Arabic),
        ('\u0750', '\u077F', SubtitleScript.Arabic),    // supplement
        ('\u0870', '\u08FF', SubtitleScript.Arabic),    // extended-A and B
        ('\uFB50', '\uFDFF', SubtitleScript.Arabic),    // presentation forms A
        ('\uFE70', '\uFEFF', SubtitleScript.Arabic),    // presentation forms B
        ('\u0E00', '\u0E7F', SubtitleScript.Thai),
        ('\u0E80', '\u0EFF', SubtitleScript.Thai),      // Lao
        ('\u1780', '\u17FF', SubtitleScript.Thai),      // Khmer
        ('\u0900', '\u0DFF', SubtitleScript.Indic),     // Devanagari through Sinhala
        ('\u3040', '\u30FF', SubtitleScript.Cjk),       // kana
        ('\u3400', '\u4DBF', SubtitleScript.Cjk),       // han, extension A
        ('\u4E00', '\u9FFF', SubtitleScript.Cjk),       // han
        ('\uAC00', '\uD7AF', SubtitleScript.Cjk)        // hangul
    };

    /// <summary>
    /// The ASS Encoding value that means "work the direction out from the text". This is
    /// what an ordinary left to right subtitle gets.
    /// </summary>
    public const int AutoEncoding = 1;

    /// <summary>
    /// The Arabic charset number (VSFilter). libass reads anything above 1 as RTL, so it works for both.
    /// </summary>
    public const int ArabicEncoding = 178;

    /// <summary>
    /// The Hebrew charset number. Right to left in libass for the same reason.
    /// </summary>
    public const int HebrewEncoding = 177;

    /// <summary>
    /// Works out the script by counting letters and taking the most common one. An Arabic subtitle is
    /// full of Latin names and song titles, so the first non-ASCII char would get it wrong.
    /// </summary>
    public static SubtitleScript Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SubtitleScript.Unknown;
        }

        var counts = new Dictionary<SubtitleScript, int>();

        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            var script = Classify(c);
            if (script == SubtitleScript.Unknown)
            {
                continue;
            }

            counts.TryGetValue(script, out var count);
            counts[script] = count + 1;
        }

        var best = SubtitleScript.Unknown;
        var bestCount = 0;

        foreach (var pair in counts)
        {
            if (pair.Value > bestCount)
            {
                bestCount = pair.Value;
                best = pair.Key;
            }
        }

        return best;
    }

    /// <summary>
    /// Gets the ASS Encoding value. Only RTL scripts get a number, in libass anything above 1 forces RTL,
    /// so Cyrillic/Greek charset numbers would reverse perfectly fine text.
    /// </summary>
    public static int GetAssEncoding(SubtitleScript script) => script switch
    {
        SubtitleScript.Arabic => ArabicEncoding,
        SubtitleScript.Hebrew => HebrewEncoding,
        _ => AutoEncoding
    };

    /// <summary>
    /// Says whether letter spacing would break the text (joined Arabic, Indic clusters, Thai marks).
    /// </summary>
    public static bool RejectsLetterSpacing(SubtitleScript script) =>
        script is SubtitleScript.Arabic or SubtitleScript.Indic or SubtitleScript.Thai;

    /// <summary>
    /// Says whether a Latin-only font like OpenDyslexic can draw this script.
    /// </summary>
    public static bool IsLatinTypefaceEnough(SubtitleScript script) =>
        script is SubtitleScript.Latin or SubtitleScript.Unknown;

    /// <summary>
    /// Gets the script's name, for telling somebody what was detected.
    /// </summary>
    public static string GetName(SubtitleScript script) => script switch
    {
        SubtitleScript.Latin => "Latin",
        SubtitleScript.Cyrillic => "Cyrillic",
        SubtitleScript.Greek => "Greek",
        SubtitleScript.Arabic => "Arabic",
        SubtitleScript.Hebrew => "Hebrew",
        SubtitleScript.Thai => "Thai",
        SubtitleScript.Indic => "Indic",
        SubtitleScript.Cjk => "Chinese, Japanese or Korean",
        _ => "unknown"
    };

    private static SubtitleScript Classify(char c)
    {
        if (c < 'A')
        {
            return SubtitleScript.Unknown;
        }

        foreach (var (start, end, script) in Ranges)
        {
            if (c >= start && c <= end)
            {
                return script;
            }
        }

        return SubtitleScript.Unknown;
    }
}
