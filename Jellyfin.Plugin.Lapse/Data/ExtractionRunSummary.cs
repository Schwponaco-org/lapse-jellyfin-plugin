// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What one run of the scheduled subtitle extraction did, for the dashboard and the log.
/// </summary>
public class ExtractionRunSummary
{
    /// <summary>
    /// Gets or sets when the run started.
    /// </summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when it finished, or null while it's still going.
    /// </summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>
    /// Gets or sets why the run did nothing, when it didn't get going at all.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it was stopped before the end.
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// Gets or sets how many video files were looked at.
    /// </summary>
    public int Videos { get; set; }

    /// <summary>
    /// Gets or sets how many of those had to be read with ffprobe.
    /// </summary>
    public int Probed { get; set; }

    /// <summary>
    /// Gets or sets how many subtitle files were written.
    /// </summary>
    public int Written { get; set; }

    /// <summary>
    /// Gets or sets how many wanted tracks had already been extracted.
    /// </summary>
    public int AlreadyExtracted { get; set; }

    /// <summary>
    /// Gets or sets how many wanted tracks were left because a subtitle file in that
    /// language was already there.
    /// </summary>
    public int CoveredByExisting { get; set; }

    /// <summary>
    /// Gets or sets how many wanted tracks turned out to be empty.
    /// </summary>
    public int Empty { get; set; }

    /// <summary>
    /// Gets or sets how many wanted tracks are pictures that can't be written out.
    /// </summary>
    public int Unsupported { get; set; }

    /// <summary>
    /// Gets or sets how many wanted tracks failed.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets the first few failures, as "file: reason", so there's something to go on
    /// without digging through the server log.
    /// </summary>
    public List<string> Failures { get; } = new();

    /// <summary>
    /// Copies the summary, so it can be sent to the dashboard while a run is still
    /// adding to it.
    /// </summary>
    /// <returns>A copy with a list of its own.</returns>
    public ExtractionRunSummary Snapshot()
    {
        var copy = new ExtractionRunSummary
        {
            StartedUtc = StartedUtc,
            FinishedUtc = FinishedUtc,
            Message = Message,
            Cancelled = Cancelled,
            Videos = Videos,
            Probed = Probed,
            Written = Written,
            AlreadyExtracted = AlreadyExtracted,
            CoveredByExisting = CoveredByExisting,
            Empty = Empty,
            Unsupported = Unsupported,
            Failed = Failed
        };

        lock (Failures)
        {
            copy.Failures.AddRange(Failures);
        }

        return copy;
    }
}
