// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// How a readable subtitle looks, written into the ASS style line so it travels with the file
/// (most clients have no font picker). Numbers are for a 1920x1080 script.
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
    /// Gets or sets extra letter spacing. Wider tracking helps readability more than the typeface does.
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
    /// Gets or sets the ASS Encoding field. 1 for left to right, the charset number for Arabic/Hebrew. See <see cref="SubtitleScripts.GetAssEncoding"/>.
    /// </summary>
    public int Encoding { get; set; } = SubtitleScripts.AutoEncoding;

    /// <summary>
    /// Gets the dyslexia-friendly preset: the installable typeface, a little larger than
    /// standard, with the tracking opened up and a heavier outline.
    /// </summary>
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
    /// Fits the style to the script. Latin-only fonts have no Arabic/Hebrew/Thai/CJK glyphs, spacing breaks
    /// joined letters, and RTL has to be marked. Size, outline and margin stay.
    /// </summary>
    public SubtitleStyle ForScript(SubtitleScript script, string? nonLatinFontName = null)
    {
        var adapted = new SubtitleStyle
        {
            FontName = FontName,
            FontSize = FontSize,
            LetterSpacing = LetterSpacing,
            Bold = Bold,
            Outline = Outline,
            MarginV = MarginV,
            Encoding = SubtitleScripts.GetAssEncoding(script)
        };

        if (SubtitleScripts.RejectsLetterSpacing(script))
        {
            adapted.LetterSpacing = 0;
        }

        // Only swap a font we know is Latin-only. If an admin typed their own font, they meant it.
        if (!SubtitleScripts.IsLatinTypefaceEnough(script) && IsLatinOnlyFont(FontName))
        {
            adapted.FontName = string.IsNullOrWhiteSpace(nonLatinFontName)
                ? DefaultFontName
                : nonLatinFontName.Trim();
        }

        return adapted;
    }

    /// <summary>
    /// Says whether a font is Latin-only. Only the one we install is on the list, it's the only one we're sure about.
    /// </summary>
    public static bool IsLatinOnlyFont(string? fontName)
    {
        return fontName is not null
            && fontName.Trim().StartsWith(DyslexicFontName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the comma separated body of a Style line, from the font name onwards, in the
    /// order the given format declared.
    /// </summary>
    public string ToStyleFields(bool advanced)
    {
        var font = SanitizeFontName(FontName);
        var size = Math.Clamp(FontSize, 8, 400).ToString(CultureInfo.InvariantCulture);
        var bold = Bold ? "-1" : "0";
        var outline = Math.Clamp(Outline, 0, 20).ToString("0.#", CultureInfo.InvariantCulture);
        var margin = Math.Clamp(MarginV, 0, 500).ToString(CultureInfo.InvariantCulture);
        var encoding = Math.Clamp(Encoding, 0, 255).ToString(CultureInfo.InvariantCulture);

        if (!advanced)
        {
            // V4: Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour,
            // BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL,
            // MarginR, MarginV, AlphaLevel, Encoding
            return $"{font},{size},&H00FFFFFF,&H000000FF,&H00000000,&H00000000,{bold},0,1,{outline},1,2,60,60,{margin},0,{encoding}";
        }

        var spacing = Math.Clamp(LetterSpacing, 0, 20).ToString("0.#", CultureInfo.InvariantCulture);

        // V4+: Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour,
        // BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle,
        // BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        return $"{font},{size},&H00FFFFFF,&H000000FF,&H00000000,&H00000000,{bold},0,0,0,100,100,{spacing},0,1,{outline},1,2,60,60,{margin},{encoding}";
    }

    // a comma or newline in the font name would shift every field after it
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
