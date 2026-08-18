// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// A request to write one of an item's subtitles out in a different format.
/// </summary>
public class ConvertRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle file to convert.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the format to write: srt, vtt, ass or ssa. Empty means whatever the
    /// Conversion settings are set to.
    /// </summary>
    public string? TargetFormat { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source file should be deleted once the
    /// converted one is written. Null means whatever the Conversion settings say, which
    /// ships as "keep it": converting should be something you can undo by deleting the
    /// new file.
    /// </summary>
    public bool? ReplaceOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the converted file then gets synced. Null
    /// means whatever the Conversion settings say, which ships as "yes" for a subtitle
    /// that had to be converted before an engine could read it.
    /// </summary>
    public bool? SyncAfter { get; set; }

    /// <summary>
    /// Gets or sets which engine a follow-on sync uses. Null for the configured default.
    /// </summary>
    public string? EngineId { get; set; }
}

/// <summary>
/// What a conversion produced.
/// </summary>
public class ConvertResult
{
    /// <summary>
    /// Gets or sets the file that was written.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the format it was written in.
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the format it was read from.
    /// </summary>
    public string SourceFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many cues came across.
    /// </summary>
    public int Cues { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source file was removed afterwards.
    /// </summary>
    public bool RemovedOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a sync ran on the converted file.
    /// </summary>
    public bool SyncedAfter { get; set; }

    /// <summary>
    /// Gets or sets the result of that sync, when one ran.
    /// </summary>
    public SyncResult? Sync { get; set; }
}
