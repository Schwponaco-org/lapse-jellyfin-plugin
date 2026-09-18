// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One engine's answer, sitting on disk as a subtitle file of its own, waiting for
/// somebody to say whether it is the right one.
/// </summary>
public class SyncCandidate
{
    /// <summary>
    /// Gets or sets which engine produced this.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets that engine's display name, so the dashboard doesn't have to look it up.
    /// </summary>
    public string EngineName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the file is.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mode this engine ran in, so keeping the answer records the same
    /// thing an ordinary sync would have.
    /// </summary>
    public SyncMode Mode { get; set; }

    /// <summary>
    /// Gets or sets how far this engine moved the subtitle, in milliseconds.
    /// </summary>
    public int? OffsetMs { get; set; }

    /// <summary>
    /// Gets or sets how much this engine stretched the subtitle, when it did.
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Gets or sets LAPSE's own verdict, for the LAPSE candidate. The other two engines
    /// report nothing of the kind, which is the whole reason this feature needs LAPSE.
    /// </summary>
    public string? Verdict { get; set; }

    /// <summary>
    /// Gets or sets a short line describing what this engine did, for the picker.
    /// </summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Every answer for one subtitle, kept together until one of them is chosen.
///
/// Nothing in here has replaced anything. The subtitle that was synced is still sitting
/// where it was, untouched, and stays that way until somebody keeps one of these or
/// throws the lot away.
/// </summary>
public class SyncCandidateSet
{
    /// <summary>
    /// Gets or sets the item these belong to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's name, so a dashboard list doesn't need a library lookup per row.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle that was synced, which is still on disk as it was.
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this set was made.
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the answers, one per engine that had something to say.
    /// </summary>
    public List<SyncCandidate> Candidates { get; } = new();
}
