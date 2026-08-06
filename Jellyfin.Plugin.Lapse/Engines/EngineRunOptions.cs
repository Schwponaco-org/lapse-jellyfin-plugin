// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Everything one engine run needs to know. This is a parameter object rather than a
/// long argument list because engines now also have to look at what the installed binary
/// supports before deciding which flags to pass.
/// </summary>
public class EngineRunOptions
{
    /// <summary>
    /// Gets or sets the video (or reference subtitle) to line up against.
    /// </summary>
    public string ReferencePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle whose timings need fixing. Engines read from here.
    /// </summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file the engine should write its result to. This is always a
    /// temporary file: the runner decides where the result finally lands, so a run that
    /// dies halfway can't leave a half written subtitle behind.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which alignment mode to use.
    /// </summary>
    public SyncMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the penalty value, only meaningful for split mode.
    /// </summary>
    public int Penalty { get; set; }

    /// <summary>
    /// Gets or sets the folder holding ffmpeg and ffprobe, or null if it couldn't be
    /// found. Engines that shell out to ffmpeg can point themselves at it.
    /// </summary>
    public string? FfmpegDirectory { get; set; }

    /// <summary>
    /// Gets or sets what the installed binary said it supports.
    /// </summary>
    public EngineRuntimeInfo Runtime { get; set; } = EngineRuntimeInfo.Unknown;
}
