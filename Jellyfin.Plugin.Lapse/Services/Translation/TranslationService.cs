// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// Runs translation jobs: read the subtitle, hand its dialogue to a provider, score what
/// comes back, and write a new file. The source subtitle is never touched, and neither is
/// the engine - translation is entirely a plugin side job that happens to be useful
/// alongside syncing.
/// </summary>
public class TranslationService
{
    private readonly IReadOnlyList<ITranslationProvider> _providers;
    private readonly ILogger<TranslationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationService"/> class.
    /// </summary>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="logger">Logger.</param>
    public TranslationService(IEnumerable<ITranslationProvider> providers, ILogger<TranslationService> logger)
    {
        // Least setup first, which is the order the dashboard lists them in and also the
        // order a fallback should try them in.
        _providers = providers.OrderBy(p => p.Tier).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Gets the providers, for the dashboard's provider picker.
    /// </summary>
    public IReadOnlyList<ITranslationProvider> Providers => _providers;

    /// <summary>
    /// Finds a provider by id.
    /// </summary>
    /// <param name="provider">Which one, or null for the configured default.</param>
    /// <returns>The provider.</returns>
    public ITranslationProvider Resolve(TranslationProvider? provider)
    {
        var wanted = provider ?? Plugin.Instance?.Configuration.DefaultTranslationProvider ?? TranslationProvider.MyMemory;
        return _providers.FirstOrDefault(p => p.Id == wanted) ?? _providers[0];
    }

    /// <summary>
    /// Translates one subtitle file into a new one.
    /// </summary>
    /// <param name="subtitlePath">The subtitle to translate. Left untouched.</param>
    /// <param name="request">The rest of the job's settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    public async Task<TranslationResult> TranslateAsync(
        string subtitlePath,
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var provider = Resolve(request.Provider);

        var result = new TranslationResult { Provider = provider.DisplayName };

        if (string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            result.Error = "Pick a language to translate into first.";
            return result;
        }

        var problem = provider.GetConfigurationProblem();
        if (problem is not null)
        {
            result.Error = problem;
            return result;
        }

        SubtitleTextFile file;
        try
        {
            file = await SubtitleTextFile.LoadAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            result.Error = ex.Message;
            return result;
        }

        var lines = file.TextLines;
        result.LineCount = lines.Count;

        if (lines.Count == 0)
        {
            result.Error = "That subtitle has no dialogue lines in it.";
            return result;
        }

        IReadOnlyList<TranslatedLine> translations;
        try
        {
            translations = await provider
                .TranslateAsync(lines, request.SourceLanguage, request.TargetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Translating {Path} with {Provider} failed", subtitlePath, provider.DisplayName);
            result.Error = ex.Message;
            return result;
        }

        var threshold = Math.Clamp(request.ConfidenceThreshold ?? config.TranslationConfidenceThreshold, 0, 100);
        var keepOriginal = request.KeepLowConfidenceOriginal ?? config.TranslationKeepLowConfidenceOriginal;
        var includeHeader = request.IncludeMetadataHeader ?? config.TranslationIncludeMetadataHeader;

        var replacements = new List<string?>(lines.Count);
        var scores = new List<double>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var translated = i < translations.Count ? translations[i] : new TranslatedLine();
            var score = Score(lines[i], translated, request.SourceLanguage);
            scores.Add(score);

            if (translated.Text is null)
            {
                replacements.Add(null);
                continue;
            }

            result.TranslatedCount++;

            if (score * 100 < threshold)
            {
                result.LowConfidenceCount++;

                // below the bar: either leave the line as it was, or take the translation
                // anyway and let the header say how many were shaky
                replacements.Add(keepOriginal ? null : translated.Text);
                continue;
            }

            replacements.Add(translated.Text);
        }

        result.AverageConfidence = scores.Count == 0 ? 0 : Math.Round(scores.Average() * 100, 1);

        file.ApplyTranslations(replacements);

        var outputPath = SubtitleTextFile.BuildOutputPath(subtitlePath, request.TargetLanguage);
        var header = includeHeader ? BuildHeader(provider, request, result, threshold) : null;

        try
        {
            await file.SaveAsync(outputPath, header, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            result.Error = "Could not write the translated file: " + ex.Message;
            return result;
        }

        result.OutputPath = outputPath;
        result.Success = true;

        _logger.LogInformation(
            "Translated {Source} to {Target} with {Provider}: {Translated}/{Total} lines, {Low} below the threshold, written to {Output}",
            subtitlePath,
            request.TargetLanguage,
            provider.DisplayName,
            result.TranslatedCount,
            result.LineCount,
            result.LowConfidenceCount,
            outputPath);

        return result;
    }

    private static IReadOnlyList<string> BuildHeader(
        ITranslationProvider provider,
        TranslationRequest request,
        TranslationResult result,
        int threshold)
    {
        return new[]
        {
            "Translated by the LAPSE Jellyfin plugin",
            "Provider: " + provider.DisplayName,
            "Date: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            "Source language: " + (string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto-detected" : request.SourceLanguage),
            "Target language: " + request.TargetLanguage,
            string.Format(
                CultureInfo.InvariantCulture,
                "Average confidence: {0}% over {1} lines, {2} below the {3}% threshold",
                result.AverageConfidence,
                result.LineCount,
                result.LowConfidenceCount,
                threshold)
        };
    }

    /// <summary>
    /// Scores one translated line from 0 to 1.
    ///
    /// MyMemory reports a match score of its own, and when a provider actually tells us
    /// how sure it is that beats anything inferred. None of the others do, so rather than
    /// pretending otherwise the plugin scores each line on what it can actually see:
    /// whether anything came back, whether the text changed at all, whether the length is
    /// plausible for a translation, and whether the language the provider detected is the
    /// one that was asked for. That's what the threshold slider filters on, and it's good
    /// enough to catch the lines that are worth a second look.
    /// </summary>
    /// <param name="source">The original line.</param>
    /// <param name="translated">What the provider said.</param>
    /// <param name="requestedSource">The source language the job asked for, if any.</param>
    /// <returns>A score from 0 to 1.</returns>
    private static double Score(string source, TranslatedLine translated, string? requestedSource)
    {
        if (string.IsNullOrWhiteSpace(translated.Text))
        {
            return 0;
        }

        if (translated.Confidence.HasValue)
        {
            return Math.Clamp(translated.Confidence.Value, 0, 1);
        }

        var trimmedSource = source.Trim();
        var trimmedTranslation = translated.Text.Trim();

        // a line with no letters at all - "♪♪", "- -", "1997" - has nothing to translate,
        // so coming back unchanged is the right answer rather than a failure
        if (!trimmedSource.Any(char.IsLetter))
        {
            return 1;
        }

        var score = 1.0;

        if (string.Equals(trimmedSource, trimmedTranslation, StringComparison.OrdinalIgnoreCase))
        {
            // identical output usually means it wasn't translated, though short lines and
            // proper nouns legitimately survive a translation unchanged
            score -= trimmedSource.Length <= 4 ? 0.25 : 0.6;
        }

        var ratio = (double)trimmedTranslation.Length / Math.Max(1, trimmedSource.Length);
        if (ratio is < 0.4 or > 2.5)
        {
            score -= 0.3;
        }

        if (!string.IsNullOrWhiteSpace(requestedSource)
            && !string.IsNullOrWhiteSpace(translated.DetectedSourceLanguage)
            && !translated.DetectedSourceLanguage.StartsWith(requestedSource, StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.2;
        }

        return Math.Clamp(score, 0, 1);
    }
}
