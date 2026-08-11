// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One advanced parameter as the settings form sends it back.
/// </summary>
public class EngineParameterEntry
{
    /// <summary>
    /// Gets or sets the parameter key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value, as text.
    /// </summary>
    public string? Value { get; set; }
}

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
    /// Gets or sets the mode a plain Sync press runs in with this engine.
    /// </summary>
    public string? DefaultMode { get; set; }

    /// <summary>
    /// Gets or sets this engine's advanced parameters.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Bound from a request body; System.Text.Json skips collection properties it can't assign to, which would make every advanced setting save silently do nothing.")]
    public List<EngineParameterEntry> Parameters { get; set; } = new();
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
    /// Gets or sets how far LAPSE's answer has to stand out before it counts as confident,
    /// in standard deviations. Passed straight to the engine as --confidence.
    /// </summary>
    public double ConfidenceSigma { get; set; } = 8;

    /// <summary>
    /// Gets or sets where a subtitle-to-subtitle result is written.
    /// </summary>
    public SubToSubPlacement SubToSubPlacement { get; set; }

    /// <summary>
    /// Gets or sets the folder used when the placement is a custom one.
    /// </summary>
    public string? SubToSubCustomFolder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether missing subtitles may be fetched from
    /// OpenSubtitles before syncing.
    /// </summary>
    public bool OpenSubtitlesEnabled { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles API key.
    /// </summary>
    public string? OpenSubtitlesApiKey { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles account name.
    /// </summary>
    public string? OpenSubtitlesUsername { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles account password.
    /// </summary>
    public string? OpenSubtitlesPassword { get; set; }

    /// <summary>
    /// Gets or sets the language to fetch in.
    /// </summary>
    public string OpenSubtitlesLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets a value indicating whether the Radarr/Sonarr webhook is accepted.
    /// </summary>
    public bool ArrWebhookEnabled { get; set; }

    /// <summary>
    /// Gets or sets the shared secret in the webhook URL. Read only from the server's
    /// point of view - the dashboard shows it and can ask for a new one, but doesn't set
    /// it by hand.
    /// </summary>
    public string? ArrWebhookToken { get; set; }

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
