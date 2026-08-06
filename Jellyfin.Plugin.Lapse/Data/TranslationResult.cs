// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What came of one translation job.
/// </summary>
public class TranslationResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the job finished.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message when <see cref="Success"/> is false.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the file that was written. Translation never overwrites its source.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets which provider did the work.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Gets or sets how many dialogue lines were in the file.
    /// </summary>
    public int LineCount { get; set; }

    /// <summary>
    /// Gets or sets how many lines came back translated.
    /// </summary>
    public int TranslatedCount { get; set; }

    /// <summary>
    /// Gets or sets how many lines scored below the confidence threshold.
    /// </summary>
    public int LowConfidenceCount { get; set; }

    /// <summary>
    /// Gets or sets the mean confidence across the file, 0-100.
    /// </summary>
    public double AverageConfidence { get; set; }
}
