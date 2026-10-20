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
/// Translation jobs: read the subtitle, send the dialogue to a provider, score what comes
/// back, write a new file. The source is never touched and no engine is involved.
/// </summary>
public class TranslationService
{
    private readonly IReadOnlyList<ITranslationProvider> _providers;
    private readonly ILogger<TranslationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationService"/> class.
    /// </summary>
    public TranslationService(IEnumerable<ITranslationProvider> providers, ILogger<TranslationService> logger)
    {
        // least setup first, same order as the dashboard
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
    public ITranslationProvider Resolve(TranslationProvider? provider)
    {
        var wanted = provider ?? Plugin.Instance?.Configuration.DefaultTranslationProvider ?? TranslationProvider.MyMemory;
        return _providers.FirstOrDefault(p => p.Id == wanted) ?? _providers[0];
    }

    /// <summary>
    /// Translates one subtitle file into a new one.
    /// </summary>
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

        if (provider.GetConfigurationProblem() is { } problem)
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

        // "auto" means nobody said, some providers accept it as a code and some don't
        var sourceLanguage = NormalizeSource(request.SourceLanguage);

        IReadOnlyList<TranslatedLine> translations;
        try
        {
            translations = await provider
                .TranslateAsync(lines, sourceLanguage, request.TargetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            // e.g. a proxy error page in front of a self hosted provider, a failed job not a crash
            _logger.LogWarning(ex, "Translating {Path} with {Provider} failed", subtitlePath, provider.DisplayName);
            result.Error = ex is System.Text.Json.JsonException
                ? provider.DisplayName + " answered with something that isn't a translation: " + ex.Message
                : ex.Message;
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
            var score = Score(lines[i], translated, sourceLanguage);
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
        var header = includeHeader ? BuildHeader(provider, sourceLanguage, request, result, threshold) : null;

        try
        {
            await file.SaveAsync(outputPath, header, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    // empty, auto, automatic, detect: let the provider figure it out
    private static string? NormalizeSource(string? sourceLanguage)
    {
        var trimmed = sourceLanguage?.Trim();

        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("automatic", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("detect", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static IReadOnlyList<string> BuildHeader(
        ITranslationProvider provider,
        string? sourceLanguage,
        TranslationRequest request,
        TranslationResult result,
        int threshold)
    {
        return new[]
        {
            "Translated by the LAPSE Jellyfin plugin",
            "Provider: " + provider.DisplayName,
            "Date: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            "Source language: " + (sourceLanguage ?? "auto-detected"),
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
    /// Scores a translated line 0..1. MyMemory gives its own score and we use that. The
    /// others don't, so it's a guess from what we can see: did anything come back, did it
    /// change, is the length plausible, and was the detected language the one asked for.
    /// Good enough to flag lines worth a second look.
    /// </summary>
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

        // no letters ("♪♪", "1997"), nothing to translate, unchanged is correct
        if (!trimmedSource.Any(char.IsLetter))
        {
            return 1;
        }

        var score = 1.0;

        if (string.Equals(trimmedSource, trimmedTranslation, StringComparison.OrdinalIgnoreCase))
        {
            // unchanged usually means untranslated, but short lines and names can legitimately stay the same
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
