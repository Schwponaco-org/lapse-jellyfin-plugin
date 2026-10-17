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
    /// Gets or sets how much of the subtitle sits on speech, 0..1. Below ~0.5 usually means wrong content. Newer LAPSE only.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Gets or sets LAPSE's verdict: "solid", "unsure" or "nothing". We gate on this rather than
    /// reinterpreting a number. Null for engines that don't say.
    /// </summary>
    public string? Verdict { get; set; }

    /// <summary>
    /// Gets or sets how many standard deviations the chosen answer beat the alternatives
    /// by, when the engine reports it. This is the number --confidence is compared against.
    /// </summary>
    public double? Sigma { get; set; }

    /// <summary>
    /// Gets or sets the share of sampled slices of the file that agreed with the answer,
    /// 0 to 1, when the engine reports it.
    /// </summary>
    public double? Agreement { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether confidence came in under the threshold. What happens next is the low confidence setting.
    /// </summary>
    public bool LowConfidence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it only went through on the --force retry, so nothing checked the answer.
    /// </summary>
    public bool Forced { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether nothing was written (low confidence with keep original,
    /// or already in sync). The run still counts as a success.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the shift was too small to bother. Unlike a doubtful skip,
    /// this counts as synced.
    /// </summary>
    public bool AlreadyInSync { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it was written next to the original because the engine
    /// wasn't sure. Not synced until someone checks it.
    /// </summary>
    public bool Unconfirmed { get; set; }

    /// <summary>
    /// Gets a value indicating whether the subtitle is synced after this (written and trusted, or already right).
    /// </summary>
    public bool CountsAsSynced => Success && (AlreadyInSync || (!Skipped && !Unconfirmed));

    /// <summary>
    /// Gets or sets the subtitle that was read, so undo knows whether to restore a backup or delete a new file.
    /// </summary>
    public string? InputPath { get; set; }

    /// <summary>
    /// Gets or sets the path to the synced subtitle file the engine wrote out.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the format before we converted it for the engine, or null. Explains a different extension.
    /// </summary>
    public string? ConvertedFrom { get; set; }

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
    /// Gets or sets a short summary of what the engine said.
    /// </summary>
    public string? EngineOutput { get; set; }

    /// <summary>
    /// Gets or sets how many multi engine answers are waiting. Zero for a normal sync.
    /// </summary>
    public int CandidateCount { get; set; }
}
