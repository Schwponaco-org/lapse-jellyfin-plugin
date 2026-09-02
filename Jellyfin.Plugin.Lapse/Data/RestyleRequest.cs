// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Asks for a readable copy of one of an item's subtitles.
/// </summary>
public class RestyleRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle to restyle. An embedded track is pulled out to a file
    /// first, same as everywhere else.
    /// </summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the styled copy replaces the subtitle it
    /// came from. Off by default: the styled file is written beside the original as a new
    /// track, so both are offered in the player and the original is still there if the
    /// styling turns out not to suit.
    /// </summary>
    public bool ReplaceOriginal { get; set; }
}

/// <summary>
/// What restyling wrote.
/// </summary>
public class RestyleResult
{
    /// <summary>
    /// Gets or sets a value indicating whether a file was written.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the file that was written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the font the style asks for.
    /// </summary>
    public string? FontName { get; set; }

    /// <summary>
    /// Gets or sets how many cues came across.
    /// </summary>
    public int Cues { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the subtitle it came from was deleted.
    /// </summary>
    public bool RemovedOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the font this asks for is actually where
    /// the player can find it. False means the file is correct but will render in
    /// whatever font the player falls back to.
    /// </summary>
    public bool FontAvailable { get; set; }

    /// <summary>
    /// Gets or sets why it didn't work, when it didn't.
    /// </summary>
    public string? Error { get; set; }
}
