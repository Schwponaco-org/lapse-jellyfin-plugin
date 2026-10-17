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
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// LibreTranslate, self hosted. Its /translate endpoint takes an array of strings and
/// returns an array back, so a whole subtitle file goes over in a handful of requests.
/// </summary>
public class LibreTranslateTranslationProvider : ITranslationProvider
{
    // batched, but small enough that one failure doesn't cost the whole file
    private const int BatchSize = 50;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibreTranslateTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibreTranslateTranslationProvider"/> class.
    /// </summary>
    public LibreTranslateTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<LibreTranslateTranslationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public TranslationProvider Id => TranslationProvider.LibreTranslate;

    /// <inheritdoc />
    public string DisplayName => "LibreTranslate";

    /// <inheritdoc />
    public int Tier => 1;

    /// <inheritdoc />
    public string Summary => "Self hosted and open source. Point this at your own instance - no cloud account involved.";

    /// <inheritdoc />
    public string? GetConfigurationProblem()
    {
        var baseUrl = Plugin.Instance?.Configuration.LibreTranslateBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "No LibreTranslate base URL is set. Add one in the LAPSE dashboard under Translation.";
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out _)
            ? null
            : $"'{baseUrl}' isn't a URL LibreTranslate can be reached at. It should look like http://libretranslate:5000.";
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

        var config = Plugin.Instance!.Configuration;
        var url = config.LibreTranslateBaseUrl!.TrimEnd('/') + "/translate";
        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage;

        var client = _httpClientFactory.CreateClient("Lapse");
        var results = new List<TranslatedLine>(lines.Count);

        foreach (var batch in TranslationBatches.Split(lines, BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = new Dictionary<string, object>
            {
                ["q"] = batch,
                ["source"] = source,
                ["target"] = targetLanguage,
                ["format"] = "text"
            };

            if (!string.IsNullOrWhiteSpace(config.LibreTranslateApiKey))
            {
                payload["api_key"] = config.LibreTranslateApiKey;
            }

            using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "LibreTranslate returned {0} for {1}: {2}",
                    (int)response.StatusCode,
                    url,
                    TranslationBatches.Shorten(body)));
            }

            using var document = await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(ReadTranslations(document, batch.Count));
        }

        return results;
    }

    private List<TranslatedLine> ReadTranslations(JsonDocument? document, int expected)
    {
        var results = new List<TranslatedLine>();

        if (document is not null && document.RootElement.TryGetProperty("translatedText", out var translated))
        {
            // some builds unwrap a one element array to a bare string
            if (translated.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in translated.EnumerateArray())
                {
                    results.Add(new TranslatedLine { Text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null });
                }
            }
            else if (translated.ValueKind == JsonValueKind.String)
            {
                results.Add(new TranslatedLine { Text = translated.GetString() });
            }
        }

        return TranslationBatches.Align(results, expected, _logger, DisplayName);
    }
}
