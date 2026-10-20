// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// How subtitles look during playback. Applied by the injected script on top of what the player
/// renders, nothing on disk changes. Each user can set their own from the subtitle menu, the
/// admin's is the default. Text subtitles only, PGS are pictures and ASS styles itself.
/// </summary>
public class SubtitleAppearance
{
    /// <summary>
    /// The font size used when nothing has been configured.
    /// </summary>
    public const int DefaultFontSizePx = 48;

    /// <summary>
    /// The text colour used when nothing has been configured.
    /// </summary>
    public const string DefaultTextColor = "#FFFFFF";

    /// <summary>
    /// The background colour used when nothing has been configured, including its alpha.
    /// </summary>
    public const string DefaultBackgroundColor = "#00000080";

    /// <summary>
    /// Gets or sets a value indicating whether the plugin restyles subtitles at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the font size in pixels.
    /// </summary>
    public int FontSizePx { get; set; } = DefaultFontSizePx;

    /// <summary>
    /// Gets or sets the text colour as a hex string.
    /// </summary>
    public string TextColor { get; set; } = DefaultTextColor;

    /// <summary>
    /// Gets or sets the background colour as a hex string, with an optional alpha pair.
    /// </summary>
    public string BackgroundColor { get; set; } = DefaultBackgroundColor;

    /// <summary>
    /// Gets or sets a value indicating whether a background is drawn behind the text.
    /// </summary>
    public bool BackgroundEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the font, or empty for the client's own. OpenDyslexic is served by the plugin once installed.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// Gets or sets extra letter spacing in px.
    /// </summary>
    public double LetterSpacingPx { get; set; }

    /// <summary>
    /// Gets or sets how far up from the bottom subtitles sit, in percent of the video height.
    /// Handy for burnt-in signs or TVs that crop. Only works for subtitles the player draws as HTML,
    /// libass draws ASS on a canvas.
    /// </summary>
    public int VerticalPositionPercent { get; set; }
}
