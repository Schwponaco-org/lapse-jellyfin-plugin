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
    /// Gets or sets the path to the synced subtitle file the engine wrote out.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the error message when <see cref="Success"/> is false.
    /// </summary>
    public string? Error { get; set; }
}
