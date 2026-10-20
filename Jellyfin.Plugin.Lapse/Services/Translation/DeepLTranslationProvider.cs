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
/// DeepL v2 REST API. Free keys end in ":fx" and use a different host, same check DeepL's
/// own libraries do, so no separate tier setting.
/// </summary>
public class DeepLTranslationProvider : ITranslationProvider
{
    // DeepL takes up to 50 text parameters per request.
    private const int BatchSize = 50;

    // DeepL wants a back-off retry on 429 (https://developers.deepl.com/docs/best-practices/error-handling),
    // a whole film is enough batches that hitting it is normal
    private const int MaxAttempts = 5;
    private const int FirstRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 30000;

    private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string ProEndpoint = "https://api.deepl.com/v2/translate";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepLTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepLTranslationProvider"/> class.
    /// </summary>
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

        foreach (var batch in TranslationBatches.Split(lines, BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            using var response = await SendWithRetriesAsync(client, url, apiKey, payload, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "DeepL returned {0}: {1}",
                    (int)response.StatusCode,
                    Describe(response.StatusCode, TranslationBatches.Shorten(body))));
            }

            using var document = await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(ReadTranslations(document, batch.Count));
        }

        return results;
    }

    // Sends one batch, backing off and trying again when DeepL says it's too busy. 429 is
    // the rate limit and 5xx is a wobble at their end; both are documented as retryable.
    // Anything else comes straight back for the caller to report.
    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpClient client,
        string url,
        string apiKey,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        var delayMs = FirstRetryDelayMs;

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);

            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!IsRetryable(response.StatusCode) || attempt >= MaxAttempts)
            {
                return response;
            }

            // DeepL can say how long to wait. When it does, that beats guessing.
            var wait = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                ?? TimeSpan.FromMilliseconds(delayMs);

            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.FromMilliseconds(delayMs);
            }

            if (wait > TimeSpan.FromMilliseconds(MaxRetryDelayMs))
            {
                wait = TimeSpan.FromMilliseconds(MaxRetryDelayMs);
            }

            _logger.LogInformation(
                "DeepL returned {Status}, waiting {Seconds:0.#}s and trying again (attempt {Attempt} of {Max})",
                (int)response.StatusCode,
                wait.TotalSeconds,
                attempt,
                MaxAttempts);

            response.Dispose();

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            // Doubling, so a key that is properly busy isn't hammered on the way out.
            delayMs = Math.Min(delayMs * 2, MaxRetryDelayMs);
        }
    }

    private static bool IsRetryable(System.Net.HttpStatusCode status)
    {
        return status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500;
    }

    // The status codes worth explaining rather than leaving as a bare number.
    private static string Describe(System.Net.HttpStatusCode status, string body)
    {
        return status switch
        {
            System.Net.HttpStatusCode.TooManyRequests =>
                "too many requests, and it was still busy after several retries. Try again later, or use a different provider for now. " + body,
            System.Net.HttpStatusCode.Forbidden =>
                "the API key was rejected. Check it under Translation in the LAPSE dashboard. " + body,
            (System.Net.HttpStatusCode)456 =>
                "the character quota on that DeepL key is used up for this billing period. " + body,
            _ => body
        };
    }

    private List<TranslatedLine> ReadTranslations(JsonDocument? document, int expected)
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

        return TranslationBatches.Align(results, expected, _logger, DisplayName);
    }
}
