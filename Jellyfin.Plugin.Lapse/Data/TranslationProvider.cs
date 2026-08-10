// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which service does the translating. The engine is not involved in any of this,
/// translation is post-processing the plugin does on a subtitle file.
///
/// The names are what ends up in the XML config, so they don't get renamed. The order
/// here doesn't decide the dashboard's order - <see cref="Services.Translation.ITranslationProvider.Tier"/>
/// does.
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
    Lingarr,

    /// <summary>
    /// MyMemory's public API. No key and no hosting, so it works out of the box.
    /// </summary>
    MyMemory,

    /// <summary>
    /// LibreTranslate, self hosted, needs a base URL.
    /// </summary>
    LibreTranslate,

    /// <summary>
    /// DeepL, free or pro, needs an API key.
    /// </summary>
    DeepL
}
