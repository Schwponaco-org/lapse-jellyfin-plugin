// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// How a subtitle should look. This gets written into an ASS file's style line rather than
/// applied in a client, which is the whole point of it: a client setting has to be found
/// and set again on every device someone watches on, and most of them have no font picker
/// at all. Styling carried by the subtitle file travels with it.
///
/// The numbers are for a 1920x1080 script, which is what the writer declares.
/// </summary>
public class SubtitleStyle
{
    /// <summary>
    /// The font used when nothing else is chosen. Matches what the plugin has always
    /// written, so an ordinary conversion is unaffected by any of this.
    /// </summary>
    public const string DefaultFontName = "Arial";

    /// <summary>
    /// The typeface LAPSE can install for the dyslexia preset. Its heavier, weighted
    /// letter bottoms are meant to make letters harder to flip or rotate by eye.
    /// </summary>
    public const string DyslexicFontName = "OpenDyslexic";

    /// <summary>
    /// Gets or sets the font name, as libass will look it up.
    /// </summary>
    public string FontName { get; set; } = DefaultFontName;

    /// <summary>
    /// Gets or sets the font size, in the 1080-tall script the writer declares.
    /// </summary>
    public int FontSize { get; set; } = 72;

    /// <summary>
    /// Gets or sets the extra space between letters, in the same units. Loosened tracking
    /// is the single change with the most evidence behind it for readability, more than
    /// the typeface itself.
    /// </summary>
    public double LetterSpacing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the text is bold.
    /// </summary>
    public bool Bold { get; set; }

    /// <summary>
    /// Gets or sets the thickness of the outline drawn around the glyphs. A heavier
    /// outline is what keeps text legible over a bright scene without a background box.
    /// </summary>
    public double Outline { get; set; } = 3;

    /// <summary>
    /// Gets or sets how far up from the bottom of the frame the text sits.
    /// </summary>
    public int MarginV { get; set; } = 60;

    /// <summary>
    /// Gets the dyslexia-friendly preset: the installable typeface, a little larger than
    /// standard, with the tracking opened up and a heavier outline.
    /// </summary>
    /// <returns>The preset.</returns>
    public static SubtitleStyle Dyslexic() => new()
    {
        FontName = DyslexicFontName,
        FontSize = 78,
        LetterSpacing = 2,
        Bold = false,
        Outline = 3.5,
        MarginV = 70
    };

    /// <summary>
    /// Builds the comma separated body of a Style line, from the font name onwards, in the
    /// order the given format declared.
    /// </summary>
    /// <param name="advanced">True for V4+ (ass), false for V4 (ssa). The two formats
    /// order their fields differently and V4 has no Spacing field at all.</param>
    /// <returns>The style line's fields, without the leading "Style: Default,".</returns>
    public string ToStyleFields(bool advanced)
    {
        var font = SanitizeFontName(FontName);
        var size = Math.Clamp(FontSize, 8, 400).ToString(CultureInfo.InvariantCulture);
        var bold = Bold ? "-1" : "0";
        var outline = Math.Clamp(Outline, 0, 20).ToString("0.#", CultureInfo.InvariantCulture);
        var margin = Math.Clamp(MarginV, 0, 500).ToString(CultureInfo.InvariantCulture);

        if (!advanced)
        {
            // V4: Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour,
            // BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL,
            // MarginR, MarginV, AlphaLevel, Encoding
            return $"{font},{size},&H00FFFFFF,&H000000FF,&H00000000,&H00000000,{bold},0,1,{outline},1,2,60,60,{margin},0,1";
        }

        var spacing = Math.Clamp(LetterSpacing, 0, 20).ToString("0.#", CultureInfo.InvariantCulture);

        // V4+: Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour,
        // BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle,
        // BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        return $"{font},{size},&H00FFFFFF,&H000000FF,&H00000000,&H00000000,{bold},0,0,0,100,100,{spacing},0,1,{outline},1,2,60,60,{margin},1";
    }

    // A comma or a newline in the font name would end the field early and shift every
    // value after it into the wrong column, which is a corrupt style line rather than an
    // odd looking one.
    private static string SanitizeFontName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultFontName;
        }

        var cleaned = name.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();

        return cleaned.Length == 0 ? DefaultFontName : cleaned;
    }
}
