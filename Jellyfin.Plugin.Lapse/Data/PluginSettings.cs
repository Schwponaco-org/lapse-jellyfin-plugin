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
    /// Gets or sets what happens to a result the engine wasn't confident about.
    /// </summary>
    public LowConfidenceAction LowConfidenceAction { get; set; }

    /// <summary>
    /// Gets or sets the confidence (0-100) a sync has to reach to count as good.
    /// </summary>
    public int SyncConfidenceThreshold { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether installed engines are kept up to date
    /// automatically.
    /// </summary>
    public bool AutoUpdateEngines { get; set; } = true;

    /// <summary>
    /// Gets or sets the Google Cloud Translation API key.
    /// </summary>
    public string? GoogleTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the DeepL API key.
    /// </summary>
    public string? DeepLApiKey { get; set; }

    /// <summary>
    /// Gets or sets the base URL of a self hosted Lingarr.
    /// </summary>
    public string? LingarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lingarr API key.
    /// </summary>
    public string? LingarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets the base URL of a self hosted LibreTranslate.
    /// </summary>
    public string? LibreTranslateBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the LibreTranslate API key.
    /// </summary>
    public string? LibreTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets how subtitles are restyled during playback.
    /// </summary>
    public SubtitleAppearance SubtitleAppearance { get; set; } = new();

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
    /// Gets or sets the per-engine settings.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "This object is bound from a request body, and System.Text.Json skips collection properties it can't assign to, which would make every engine tuning save silently do nothing.")]
    public List<EngineSettingsEntry> Engines { get; set; } = new();
}
