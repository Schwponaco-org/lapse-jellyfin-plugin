// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One external subtitle file that could be synced, shown in the subtitle picker
/// when a movie has more than one.
/// </summary>
public class SubtitleOption
{
    /// <summary>
    /// Gets or sets the full path to the subtitle file on disk.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human readable label for the picker, usually the language plus the file name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle language, if Jellyfin knows it.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the format, without the dot: srt, vtt, ass, ssa, sub and so on.
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it needs converting before the default engine can read it.
    /// </summary>
    public bool NeedsConversion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can be synced at all (PGS can, if the engine reads it).
    /// </summary>
    public bool Supported { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether there's text in it (false for PGS/VobSub). Convert, translate and shift need this.
    /// </summary>
    public bool TextBased { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether it's still inside the video. Gets extracted the first time something needs it.
    /// </summary>
    public bool IsEmbedded { get; set; }

    /// <summary>
    /// Gets or sets the codec of an embedded track, as Jellyfin reported it.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets a value indicating whether this file's timings can be shifted by hand.
    /// </summary>
    public bool Shiftable => !IsEmbedded && Services.SubtitleFormats.IsNative(Path);
}
