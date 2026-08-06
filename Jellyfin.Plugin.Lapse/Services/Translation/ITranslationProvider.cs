// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// One translated line, as the provider handed it back.
/// </summary>
public class TranslatedLine
{
    /// <summary>
    /// Gets or sets the translated text, or null if the provider couldn't translate it.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets the language the provider says it detected, when it says anything.
    /// </summary>
    public string? DetectedSourceLanguage { get; set; }
}

/// <summary>
/// Something that can turn subtitle lines into another language.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>
    /// Gets which provider this is.
    /// </summary>
    TranslationProvider Id { get; }

    /// <summary>
    /// Gets the display name for messages.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Checks the plugin has what this provider needs to work.
    /// </summary>
    /// <returns>Null if it's configured, otherwise what's missing.</returns>
    string? GetConfigurationProblem();

    /// <summary>
    /// Translates a batch of lines.
    /// </summary>
    /// <param name="lines">The lines to translate, in file order.</param>
    /// <param name="sourceLanguage">The source language code, or null to auto-detect.</param>
    /// <param name="targetLanguage">The language code to translate into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per input line, in the same order.</returns>
    Task<IReadOnlyList<TranslatedLine>> TranslateAsync(
        IReadOnlyList<string> lines,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
