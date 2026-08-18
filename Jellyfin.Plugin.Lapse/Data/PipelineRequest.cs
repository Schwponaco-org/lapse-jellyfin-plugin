// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// A request to do several things to one subtitle in one press: change its format, line
/// it up, and translate it. Nothing here happens unless it's asked for, and the order is
/// fixed because it's the only order that makes sense - convert first so the engine can
/// read the file, sync next so the translation inherits the corrected timings, translate
/// last.
/// </summary>
public class PipelineRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle to work on. An embedded track is pulled out of the video
    /// first, the same as anywhere else.
    /// </summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets or sets the format to end up in (srt, vtt, ass, ssa), or null to keep the one
    /// it started in.
    /// </summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Gets or sets where the result lands, or null for the configured default.
    /// </summary>
    public OutputMode? OutputMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to sync.
    /// </summary>
    public bool Sync { get; set; } = true;

    /// <summary>
    /// Gets or sets which engine to sync with, or null for the default.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets the alignment mode, or null for the engine's own default.
    /// </summary>
    public SyncMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the split penalty. Only used in split mode.
    /// </summary>
    public int Penalty { get; set; }

    /// <summary>
    /// Gets or sets the translation to run afterwards, or null to skip translating.
    /// </summary>
    public TranslationRequest? Translation { get; set; }
}

/// <summary>
/// What a pipeline run produced, step by step. Any step that didn't run is null.
/// </summary>
public class PipelineResult
{
    /// <summary>
    /// Gets or sets the file the run ended up working on, after any extraction or
    /// conversion.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the format the subtitle was in before a conversion, or null if none
    /// was needed.
    /// </summary>
    public string? ConvertedFrom { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the track was pulled out of the video.
    /// </summary>
    public bool Extracted { get; set; }

    /// <summary>
    /// Gets or sets the sync result, when a sync ran.
    /// </summary>
    public SyncResult? Sync { get; set; }

    /// <summary>
    /// Gets or sets the translation result, when a translation ran.
    /// </summary>
    public TranslationResult? Translation { get; set; }

    /// <summary>
    /// Gets or sets what stopped the run, when something did.
    /// </summary>
    public string? Error { get; set; }
}
