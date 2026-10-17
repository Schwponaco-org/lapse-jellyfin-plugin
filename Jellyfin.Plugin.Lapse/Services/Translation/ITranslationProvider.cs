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

    /// <summary>
    /// Gets or sets the provider's own confidence 0..1. Only MyMemory gives one, otherwise null and we score it ourselves.
    /// </summary>
    public double? Confidence { get; set; }
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
    /// Gets how much setup it needs: 0 none, 1 a self hosted service, 2 a cloud API key. The dashboard sorts by this.
    /// </summary>
    int Tier { get; }

    /// <summary>
    /// Gets a line explaining what this provider is and what it wants, for the dashboard.
    /// </summary>
    string Summary { get; }

    /// <summary>
    /// Checks the plugin has what this provider needs to work.
    /// </summary>
    string? GetConfigurationProblem();

    /// <summary>
    /// Translates a batch of lines.
    /// </summary>
    Task<IReadOnlyList<TranslatedLine>> TranslateAsync(
        IReadOnlyList<string> lines,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
