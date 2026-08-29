// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What the translation boxes should start on. Sent to the dialogs so a manual run uses
/// the same settings an unattended one does.
/// </summary>
public class TranslationDefaults
{
    /// <summary>
    /// Gets or sets the language to translate into, or null if none is set.
    /// </summary>
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// Gets or sets the language the subtitle is assumed to be in, or null for automatic
    /// detection.
    /// </summary>
    public string? SourceLanguage { get; set; }

    /// <summary>
    /// Gets or sets the provider to start on.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Gets or sets the confidence threshold, 0-100.
    /// </summary>
    public int ConfidenceThreshold { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the metadata comment block is wanted.
    /// </summary>
    public bool IncludeMetadataHeader { get; set; }
}
