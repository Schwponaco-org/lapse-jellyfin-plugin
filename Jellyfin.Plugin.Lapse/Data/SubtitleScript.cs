// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// The writing system a subtitle's dialogue is in. Restyling has to know this: the
/// readable preset is built around the Latin alphabet, and applying it unchanged to
/// Arabic or Thai makes those harder to read rather than easier.
/// </summary>
public enum SubtitleScript
{
    /// <summary>
    /// Nothing to go on - an empty file, or one with no letters in it at all. Treated as
    /// Latin, which is what it was before any of this existed.
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
    /// Arabic, and the languages that borrow its alphabet - Persian, Urdu, Pashto. Written
    /// right to left, and its letters join up, which is why spacing them apart is wrong.
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
    /// The Indic abugidas - Devanagari, Bengali, Tamil and their neighbours. Letters
    /// combine into clusters, which pulling them apart breaks.
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
    // Where each script lives in Unicode, written as escapes so the table stays readable
    // in an editor that would otherwise reorder the right to left rows on screen. Only the
    // blocks worth telling apart are here - the point is picking a font and a direction,
    // not cataloguing Unicode.
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
    /// The Arabic charset number, as VSFilter and every ASS file in the wild use it.
    /// libass reads anything above 1 as "this paragraph runs right to left", which is the
    /// same answer by a different route, so the one number is right for both renderers.
    /// </summary>
    public const int ArabicEncoding = 178;

    /// <summary>
    /// The Hebrew charset number. Right to left in libass for the same reason.
    /// </summary>
    public const int HebrewEncoding = 177;

    /// <summary>
    /// Works out the writing system a piece of subtitle text is in by counting letters and
    /// taking whichever script has the most of them.
    ///
    /// Counting rather than looking at the first non-ASCII character matters, because an
    /// Arabic subtitle is full of Latin - song titles, names, the odd English line - and a
    /// Danish one is almost all ASCII. The majority is what the styling should follow.
    /// </summary>
    /// <param name="text">The dialogue. Timestamps and tags can be left in; they are
    /// ASCII punctuation and digits, which aren't counted either way.</param>
    /// <returns>The dominant script, or <see cref="SubtitleScript.Unknown"/> when there
    /// are no letters at all.</returns>
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
    /// Gets the ASS Encoding value to write for a script.
    ///
    /// Only the right to left scripts get a number of their own. In libass every value
    /// above 1 forces the paragraph right to left, so writing the Cyrillic or Greek
    /// charset numbers that VSFilter defines would reverse text that reads perfectly well
    /// left to right. Those stay on the automatic setting, which is correct for them.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>The Encoding field's value.</returns>
    public static int GetAssEncoding(SubtitleScript script) => script switch
    {
        SubtitleScript.Arabic => ArabicEncoding,
        SubtitleScript.Hebrew => HebrewEncoding,
        _ => AutoEncoding
    };

    /// <summary>
    /// Says whether extra space between letters would damage the text rather than open it
    /// up. True where letters join into one another or stack marks on each other: Arabic
    /// joins, the Indic scripts form clusters, and Thai hangs vowels and tones off the
    /// consonant they belong to. Pulling those apart isn't wider tracking, it's broken
    /// words.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>True when letter spacing has to be left at zero.</returns>
    public static bool RejectsLetterSpacing(SubtitleScript script) =>
        script is SubtitleScript.Arabic or SubtitleScript.Indic or SubtitleScript.Thai;

    /// <summary>
    /// Says whether a Latin-only typeface - which is what OpenDyslexic is - has the glyphs
    /// for this script. When it doesn't, asking for it gives a line of empty boxes or a
    /// silent substitution, so the font has to be swapped for one that covers the script.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>True when a Latin typeface can render it.</returns>
    public static bool IsLatinTypefaceEnough(SubtitleScript script) =>
        script is SubtitleScript.Latin or SubtitleScript.Unknown;

    /// <summary>
    /// Gets the script's name, for telling somebody what was detected.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>A word for it.</returns>
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
