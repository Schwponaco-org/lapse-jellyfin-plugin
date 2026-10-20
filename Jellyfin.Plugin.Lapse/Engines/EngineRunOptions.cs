// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Everything one engine run needs.
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
    /// Gets or sets where the engine writes. Always a temp file, the runner moves it into place.
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
    /// Gets or sets the ffmpeg/ffprobe folder, or null if not found.
    /// </summary>
    public string? FfmpegDirectory { get; set; }

    /// <summary>
    /// Gets or sets what the installed binary said it supports.
    /// </summary>
    public EngineRuntimeInfo Runtime { get; set; } = EngineRuntimeInfo.Unknown;

    /// <summary>
    /// Gets or sets a value indicating whether to sync even with too few cues to judge (the runner's retry).
    /// </summary>
    public bool ForceAnyway { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine has to listen to the audio
    /// rather than line up against a subtitle track inside the video. Set when the
    /// subtitle being synced was taken out of that same video: lined up against itself,
    /// it always comes back "already in sync", however far out it is.
    /// </summary>
    public bool IgnoreEmbedded { get; set; }

    /// <summary>
    /// Gets or sets this engine's advanced parameters, already merged with the engine's
    /// own defaults.
    /// </summary>
    public EngineParameterValues Parameters { get; set; } = EngineParameterValues.Empty;

    /// <summary>
    /// Gets or sets how far the answer has to stand out before the engine will overwrite
    /// the original, in standard deviations. Only LAPSE takes this (as --confidence); the
    /// other engines report no confidence at all and ignore it.
    /// </summary>
    public double ConfidenceSigma { get; set; } = LapseEngine.DefaultConfidenceSigma;
}
