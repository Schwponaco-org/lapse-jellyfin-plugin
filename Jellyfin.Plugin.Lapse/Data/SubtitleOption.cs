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
    /// Gets or sets a value indicating whether this file has to be converted before an
    /// engine can touch it. Depends on which engine: LAPSE reads MicroDVD .sub and eight
    /// other formats directly, where alass and ffsubsync read none of them.
    /// </summary>
    public bool NeedsConversion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin can do anything with this file
    /// at all - which now means "can it be synced", since a picture based subtitle has no
    /// text but can still have its timing rewritten by an engine that reads the format.
    /// </summary>
    public bool Supported { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether there is text in this file the plugin can
    /// get at. False for PGS and VobSub, which are pictures. Converting, translating and
    /// shifting by hand all need this; syncing does not.
    /// </summary>
    public bool TextBased { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this track is still inside the video file
    /// rather than sitting beside it. Those get pulled out to a real file the first time
    /// something needs to work on them.
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
