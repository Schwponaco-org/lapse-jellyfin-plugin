// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One translation job, as the dashboard or the context menu asks for it.
/// </summary>
public class TranslationRequest
{
    /// <summary>
    /// Gets or sets the item the subtitle belongs to, so the path can be checked against
    /// what the library actually lists for it.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the subtitle file to translate.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source language code, or null/empty to let the provider detect it.
    /// </summary>
    public string? SourceLanguage { get; set; }

    /// <summary>
    /// Gets or sets the language code to translate into, e.g. "es".
    /// </summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which provider to use, or null for the configured default.
    /// </summary>
    public TranslationProvider? Provider { get; set; }

    /// <summary>
    /// Gets or sets the confidence threshold (0-100), or null for the configured default.
    /// </summary>
    public int? ConfidenceThreshold { get; set; }

    /// <summary>
    /// Gets or sets whether to put a metadata comment block at the top of the output, or
    /// null for the configured default.
    /// </summary>
    public bool? IncludeMetadataHeader { get; set; }

    /// <summary>
    /// Gets or sets whether lines below the threshold keep their original text, or null
    /// for the configured default. When false they're translated anyway and counted as
    /// flagged in the header.
    /// </summary>
    public bool? KeepLowConfidenceOriginal { get; set; }
}
