// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What pulling the subtitle tracks out of a video file did.
/// </summary>
public class RemuxResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the video came out the other side with
    /// fewer subtitle tracks in it than it went in with.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the video file the tracks now live beside. The original path when the
    /// run replaced it, the new .nosubs file when it didn't.
    /// </summary>
    public string? VideoPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the original video file was replaced. False
    /// means it is still there and the result was written alongside it.
    /// </summary>
    public bool ReplacedOriginal { get; set; }

    /// <summary>
    /// Gets the subtitle files that were written out of the video before the tracks were
    /// dropped from it.
    /// </summary>
    public List<string> ExtractedPaths { get; } = new();

    /// <summary>
    /// Gets the tracks that were left in the video because nothing could be saved out of
    /// them - PGS and VobSub, which are pictures rather than text. Dropping a track that
    /// could not be extracted would destroy it, so those stay put.
    /// </summary>
    public List<string> KeptTracks { get; } = new();

    /// <summary>
    /// Gets or sets how many subtitle tracks were dropped from the video.
    /// </summary>
    public int RemovedCount { get; set; }

    /// <summary>
    /// Gets or sets why it didn't work, when it didn't.
    /// </summary>
    public string? Error { get; set; }
}
