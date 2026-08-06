// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One engine's tunables as the settings form sends them back.
/// </summary>
public class EngineSettingsEntry
{
    /// <summary>
    /// Gets or sets which engine this is for.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom path to the binary, or null to use the installed copy.
    /// </summary>
    public string? PathOverride { get; set; }

    /// <summary>
    /// Gets or sets the split penalty, or null for the engine's own default.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the daily task may update this engine.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;
}

/// <summary>
/// The settings the dashboard shows outside the libraries table. Saved through a plugin
/// endpoint rather than by posting the whole plugin configuration back, so the sync
/// history and the skip list can't get clobbered by a form that never knew about them.
/// </summary>
public class PluginSettings
{
    /// <summary>
    /// Gets or sets where synced subtitles get written.
    /// </summary>
    public OutputMode OutputMode { get; set; }

    /// <summary>
    /// Gets or sets what goes before the extension in the sidecar output modes.
    /// </summary>
    public string SidecarSuffix { get; set; } = ".shifted";

    /// <summary>
    /// Gets or sets the Google Cloud Translation API key.
    /// </summary>
    public string? GoogleTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the base URL of a self hosted Lingarr.
    /// </summary>
    public string? LingarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lingarr API key.
    /// </summary>
    public string? LingarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets which translation provider is used by default.
    /// </summary>
    public TranslationProvider DefaultTranslationProvider { get; set; }

    /// <summary>
    /// Gets or sets the default confidence threshold, 0-100.
    /// </summary>
    public int TranslationConfidenceThreshold { get; set; } = 70;

    /// <summary>
    /// Gets or sets a value indicating whether translated files get a metadata header.
    /// </summary>
    public bool TranslationIncludeMetadataHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether low confidence lines keep their original text.
    /// </summary>
    public bool TranslationKeepLowConfidenceOriginal { get; set; }

    /// <summary>
    /// Gets the per-engine settings.
    /// </summary>
    public List<EngineSettingsEntry> Engines { get; } = new();
}
