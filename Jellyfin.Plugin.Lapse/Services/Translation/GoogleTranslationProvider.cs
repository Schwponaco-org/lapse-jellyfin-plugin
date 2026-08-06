// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// Google Cloud Translation, the v2 REST API. It takes a batch of strings per request,
/// which is a lot kinder to both the rate limit and the clock than one call per line.
/// </summary>
public class GoogleTranslationProvider : ITranslationProvider
{
    // Google caps a v2 request at 128 strings, and the whole request at 30k characters.
    // 64 short subtitle lines sits comfortably inside both.
    private const int BatchSize = 64;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleTranslationProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public GoogleTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<GoogleTranslationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public TranslationProvider Id => TranslationProvider.Google;

    /// <inheritdoc />
    public string DisplayName => "Google Translate";

    /// <inheritdoc />
    public string? GetConfigurationProblem()
    {
        var key = Plugin.Instance?.Configuration.GoogleTranslateApiKey;
        return string.IsNullOrWhiteSpace(key)
            ? "No Google Translate API key is set. Add one in the LAPSE dashboard under Translation."
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranslatedLine>> TranslateAsync(
        IReadOnlyList<string> lines,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var problem = GetConfigurationProblem();
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        var apiKey = Plugin.Instance!.Configuration.GoogleTranslateApiKey!;
        var client = _httpClientFactory.CreateClient("Lapse");
        var results = new List<TranslatedLine>(lines.Count);

        for (var start = 0; start < lines.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new List<string>();
            for (var i = start; i < Math.Min(start + BatchSize, lines.Count); i++)
            {
                batch.Add(lines[i]);
            }

            var url = "https://translation.googleapis.com/language/translate/v2?key=" + HttpUtility.UrlEncode(apiKey);
            var payload = new Dictionary<string, object>
            {
                ["q"] = batch,
                ["target"] = targetLanguage,
                ["format"] = "text"
            };

            if (!string.IsNullOrWhiteSpace(sourceLanguage))
            {
                payload["source"] = sourceLanguage;
            }

            using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Google Translate returned {0}: {1}",
                    (int)response.StatusCode,
                    Shorten(body)));
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            results.AddRange(ReadTranslations(document, batch.Count));
        }

        return results;
    }

    private IEnumerable<TranslatedLine> ReadTranslations(JsonDocument? document, int expected)
    {
        var results = new List<TranslatedLine>();

        if (document is not null
            && document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("translations", out var translations)
            && translations.ValueKind == JsonValueKind.Array)
        {
            foreach (var translation in translations.EnumerateArray())
            {
                results.Add(new TranslatedLine
                {
                    Text = translation.TryGetProperty("translatedText", out var text) ? text.GetString() : null,
                    DetectedSourceLanguage = translation.TryGetProperty("detectedSourceLanguage", out var detected)
                        ? detected.GetString()
                        : null
                });
            }
        }

        // Google returns one translation per input, but if it ever doesn't, pad the batch
        // out so the results still line up with the lines they came from
        if (results.Count != expected)
        {
            _logger.LogWarning("Google Translate returned {Got} translations for {Expected} lines", results.Count, expected);
            while (results.Count < expected)
            {
                results.Add(new TranslatedLine());
            }
        }

        return results;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
