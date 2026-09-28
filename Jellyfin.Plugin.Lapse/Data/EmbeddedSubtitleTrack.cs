// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One subtitle stream inside a video file, as the file itself describes it. Built from
/// ffprobe's answer when the file has been looked at directly, or from Jellyfin's own
/// record of the stream when it hasn't.
/// </summary>
public class EmbeddedSubtitleTrack
{
    /// <summary>
    /// Gets or sets the stream's index in the container, the number ffmpeg's -map 0:N
    /// takes.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the codec name as ffmpeg reports it: subrip, ass, mov_text,
    /// hdmv_pgs_subtitle and so on.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets or sets the language tag exactly as the file has it, or null when there isn't
    /// one.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the track's title, which is where a lot of releases say "Forced" or
    /// "SDH" instead of setting the flag.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file flags the track as forced.
    /// </summary>
    public bool ForcedFlag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file flags the track as being for the
    /// hard of hearing.
    /// </summary>
    public bool HearingImpairedFlag { get; set; }

    /// <summary>
    /// Gets a value indicating whether the track counts as forced: flagged as such, or
    /// titled as such.
    /// </summary>
    public bool IsForced => ForcedFlag || Services.SubtitleTrackFlags.TitleSaysForced(Title);

    /// <summary>
    /// Gets a value indicating whether the track counts as SDH: flagged as such, or
    /// titled as such.
    /// </summary>
    public bool IsHearingImpaired => HearingImpairedFlag || Services.SubtitleTrackFlags.TitleSaysHearingImpaired(Title);
}
