// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Result of one LAPSE engine run, parsed from its stdout.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the engine run succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets which mode the engine actually ran in.
    /// </summary>
    public SyncMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the constant offset in milliseconds, only set for Standard (nosplit) runs.
    /// </summary>
    public int? OffsetMs { get; set; }

    /// <summary>
    /// Gets or sets the slope, only set for Standard OLS runs.
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Gets or sets the intercept in seconds, only set for Standard OLS runs.
    /// </summary>
    public double? Intercept { get; set; }

    /// <summary>
    /// Gets or sets the penalty that was used, only set for split runs.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets how much of the subtitle ended up sitting on speech, 0 to 1. Newer
    /// LAPSE builds report this; anything below about 0.5 usually means the subtitle and
    /// the video aren't the same content. Null when the engine didn't say.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine's confidence came in under the
    /// configured threshold. What actually happened to the result then depends on the
    /// low-confidence setting in File output.
    /// </summary>
    public bool LowConfidence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the result was thrown away and the file on
    /// disk left exactly as it was. Only happens for a low-confidence result under the
    /// "keep original" setting - the run itself still counts as a success, it just
    /// deliberately didn't write anything.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Gets or sets the path to the synced subtitle file the engine wrote out.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the path of the backup that was taken before overwriting, if the
    /// output mode asked for one.
    /// </summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// Gets or sets the error message when <see cref="Success"/> is false.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the id of the engine that ran this sync.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets a short line of whatever the engine said. Used for engines where we
    /// don't have a documented output format to pull exact numbers out of, so there's
    /// still something meaningful to show the user.
    /// </summary>
    public string? EngineOutput { get; set; }
}
