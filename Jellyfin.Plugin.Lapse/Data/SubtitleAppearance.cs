// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// How subtitles should look during playback. This is applied by the injected script
/// restyling what the web player already renders, so nothing on disk is touched and
/// turning it off puts everything straight back.
///
/// Every user can have their own, set from the subtitle menu while a film is playing.
/// That is the point of it: a shared library has one person who wants huge text in a
/// dyslexia-friendly font and several who don't, and a server-wide setting can only ever
/// suit one of them. What an admin saves in the dashboard is the starting point for
/// anybody who hasn't set their own.
///
/// Only text based formats can be restyled. PGS and VOBSUB are pictures of subtitles, so
/// there is nothing here to apply to them, and ASS/SSA carries its own per-line styling
/// which the player honours over any global override.
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
    /// Gets or sets the font the player draws subtitles in, or empty for whatever the
    /// client already uses. Any font the browser can resolve works; OpenDyslexic is
    /// served by the plugin once an admin has installed it, so it can be picked here
    /// without anything being installed on the device.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// Gets or sets the extra space between letters, in pixels. Wider tracking is the
    /// readability change with the most evidence behind it, and unlike the file-based
    /// styling it costs nothing here to undo.
    /// </summary>
    public double LetterSpacingPx { get; set; }

    /// <summary>
    /// Returns a copy, so a user's own settings can be edited without writing through to
    /// the server-wide defaults they were copied from.
    /// </summary>
    /// <returns>A copy of these settings.</returns>
    public SubtitleAppearance Clone() => new()
    {
        Enabled = Enabled,
        FontSizePx = FontSizePx,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
        BackgroundEnabled = BackgroundEnabled,
        FontFamily = FontFamily,
        LetterSpacingPx = LetterSpacingPx
    };
}
