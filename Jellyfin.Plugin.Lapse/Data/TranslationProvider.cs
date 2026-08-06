// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which service does the translating. The engine is not involved in any of this,
/// translation is post-processing the plugin does on a subtitle file.
/// </summary>
public enum TranslationProvider
{
    /// <summary>
    /// Google Cloud Translation, needs an API key.
    /// </summary>
    Google,

    /// <summary>
    /// Lingarr, self hosted, needs a base URL.
    /// </summary>
    Lingarr
}
