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
/// MyMemory's public translation API, the documented one at mymemory.translated.net. It
/// needs no key and no hosting, which makes it the only provider that works on a fresh
/// install, so it's the default.
///
/// Its API is one line per request and it rate limits anonymous callers by the day, so
/// this paces itself and gives up with a clear message rather than hammering it. It's
/// also the only provider here that hands back a real confidence number, which the
/// threshold slider then means something concrete against.
/// </summary>
public class MyMemoryTranslationProvider : ITranslationProvider
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    // Their guidance for anonymous use is to keep it gentle. A small gap between calls
    // keeps a long subtitle file from tripping the daily limit halfway through.
    private static readonly TimeSpan BetweenCalls = TimeSpan.FromMilliseconds(120);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MyMemoryTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MyMemoryTranslationProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public MyMemoryTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<MyMemoryTranslationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public TranslationProvider Id => TranslationProvider.MyMemory;

    /// <inheritdoc />
    public string DisplayName => "MyMemory";

    /// <inheritdoc />
    public int Tier => 0;

    /// <inheritdoc />
    public string Summary => "Works straight away, no account and nothing to host. Rate limited, so a long subtitle file takes a while.";

    /// <inheritdoc />
    public string? GetConfigurationProblem()
    {
        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranslatedLine>> TranslateAsync(
        IReadOnlyList<string> lines,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        // MyMemory always wants a source language, it has no auto-detect. English is the
        // overwhelmingly common source for subtitles people want translated, and its own
        // matching is forgiving enough that a wrong guess degrades rather than fails.
        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "en" : sourceLanguage;
        var pair = source + "|" + targetLanguage;

        var client = _httpClientFactory.CreateClient("Lapse");
        var results = new List<TranslatedLine>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                results.Add(new TranslatedLine { Text = lines[i] });
                continue;
            }

            var url = Endpoint
                + "?q=" + HttpUtility.UrlEncode(lines[i])
                + "&langpair=" + HttpUtility.UrlEncode(pair);

            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "MyMemory returned {0} after {1} of {2} lines: {3}",
                    (int)response.StatusCode,
                    i,
                    lines.Count,
                    Shorten(body)));
            }

            using var document = await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false);

            results.Add(ReadTranslation(document, source));

            if (i < lines.Count - 1)
            {
                await Task.Delay(BetweenCalls, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    private TranslatedLine ReadTranslation(JsonDocument? document, string source)
    {
        if (document is null || !document.RootElement.TryGetProperty("responseData", out var data))
        {
            return new TranslatedLine();
        }

        // A quota message comes back with a 200 and the text of the complaint where the
        // translation should be, so treat that as the failure it is rather than writing
        // "MYMEMORY WARNING" into someone's subtitle file.
        if (document.RootElement.TryGetProperty("responseStatus", out var status)
            && TryReadStatus(status, out var statusCode)
            && statusCode != 200)
        {
            var detail = document.RootElement.TryGetProperty("responseDetails", out var details)
                ? details.GetString()
                : null;

            throw new HttpRequestException("MyMemory refused the request: " + (detail ?? "no reason given"));
        }

        var text = data.TryGetProperty("translatedText", out var translated) ? translated.GetString() : null;

        if (text is not null && text.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("MyMemory returned a warning instead of a translation: {Text}", text);
            throw new HttpRequestException("MyMemory has stopped translating for now: " + text);
        }

        return new TranslatedLine
        {
            Text = text,
            DetectedSourceLanguage = source,

            // MyMemory scores its own match from 0 to 1, which is a far better answer
            // than anything the plugin could infer from the text alone.
            Confidence = data.TryGetProperty("match", out var match) ? ReadMatch(match) : null
        };
    }

    private static bool TryReadStatus(JsonElement status, out int value)
    {
        switch (status.ValueKind)
        {
            case JsonValueKind.Number:
                return status.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(status.GetString(), CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static double? ReadMatch(JsonElement match)
    {
        return match.ValueKind switch
        {
            JsonValueKind.Number when match.TryGetDouble(out var number) => Math.Clamp(number, 0, 1),
            JsonValueKind.String when double.TryParse(match.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => Math.Clamp(parsed, 0, 1),
            _ => null
        };
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
