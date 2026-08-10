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
/// DeepL, through its documented v2 REST API. Free and Pro keys use different hosts and
/// are told apart by the ":fx" suffix a free key carries, which is exactly how DeepL's
/// own client libraries decide, so there's no separate "which tier am I" setting to get
/// wrong.
/// </summary>
public class DeepLTranslationProvider : ITranslationProvider
{
    // DeepL takes up to 50 text parameters per request.
    private const int BatchSize = 50;

    private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string ProEndpoint = "https://api.deepl.com/v2/translate";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepLTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepLTranslationProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public DeepLTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<DeepLTranslationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public TranslationProvider Id => TranslationProvider.DeepL;

    /// <inheritdoc />
    public string DisplayName => "DeepL";

    /// <inheritdoc />
    public int Tier => 2;

    /// <inheritdoc />
    public string Summary => "Usually the best quality of the lot. Needs an API key; free keys end in \":fx\" and are detected automatically.";

    /// <inheritdoc />
    public string? GetConfigurationProblem()
    {
        var key = Plugin.Instance?.Configuration.DeepLApiKey;
        return string.IsNullOrWhiteSpace(key)
            ? "No DeepL API key is set. Add one in the LAPSE dashboard under Translation."
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

        var apiKey = Plugin.Instance!.Configuration.DeepLApiKey!.Trim();
        var url = apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? FreeEndpoint : ProEndpoint;

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

            var payload = new Dictionary<string, object>
            {
                ["text"] = batch,

                // DeepL wants the target as an upper case code, and it distinguishes
                // regional variants like EN-GB, so pass whatever was typed through
                // uppercased rather than trying to be clever about it.
                ["target_lang"] = targetLanguage.ToUpperInvariant()
            };

            if (!string.IsNullOrWhiteSpace(sourceLanguage))
            {
                payload["source_lang"] = sourceLanguage.ToUpperInvariant();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "DeepL returned {0}: {1}",
                    (int)response.StatusCode,
                    Shorten(body)));
            }

            using var document = await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(ReadTranslations(document, batch.Count));
        }

        return results;
    }

    private IEnumerable<TranslatedLine> ReadTranslations(JsonDocument? document, int expected)
    {
        var results = new List<TranslatedLine>();

        if (document is not null
            && document.RootElement.TryGetProperty("translations", out var translations)
            && translations.ValueKind == JsonValueKind.Array)
        {
            foreach (var translation in translations.EnumerateArray())
            {
                results.Add(new TranslatedLine
                {
                    Text = translation.TryGetProperty("text", out var text) ? text.GetString() : null,
                    DetectedSourceLanguage = translation.TryGetProperty("detected_source_language", out var detected)
                        ? detected.GetString()
                        : null
                });
            }
        }

        if (results.Count != expected)
        {
            _logger.LogWarning("DeepL returned {Got} translations for {Expected} lines", results.Count, expected);
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
