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
/// MyMemory (mymemory.translated.net). No key, nothing to host, so it's the default and
/// works on a fresh install. One line per request and a daily limit for anonymous use, so
/// it paces itself. Only provider that returns a real confidence score.
/// </summary>
public class MyMemoryTranslationProvider : ITranslationProvider
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    // be gentle so a long file doesn't hit the limit halfway through
    private static readonly TimeSpan BetweenCalls = TimeSpan.FromMilliseconds(120);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MyMemoryTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MyMemoryTranslationProvider"/> class.
    /// </summary>
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
        // no auto-detect here, English is by far the most common source anyway
        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "en" : sourceLanguage;
        var pair = source + "|" + targetLanguage;

        var client = _httpClientFactory.CreateClient("Lapse");
        var results = new List<TranslatedLine>(lines.Count);

        // subtitles repeat themselves a lot ("What?", "Yeah."), only ask once per line
        var seen = new Dictionary<string, TranslatedLine>(StringComparer.Ordinal);
        var asked = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                results.Add(new TranslatedLine { Text = lines[i] });
                continue;
            }

            if (seen.TryGetValue(lines[i], out var repeat))
            {
                results.Add(repeat);
                continue;
            }

            if (asked++ > 0)
            {
                await Task.Delay(BetweenCalls, cancellationToken).ConfigureAwait(false);
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
                    TranslationBatches.Shorten(body)));
            }

            using var document = await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false);

            var line = ReadTranslation(document, source);
            seen[lines[i]] = line;
            results.Add(line);
        }

        return results;
    }

    private TranslatedLine ReadTranslation(JsonDocument? document, string source)
    {
        if (document is null || !document.RootElement.TryGetProperty("responseData", out var data))
        {
            return new TranslatedLine();
        }

        // quota errors come back as a 200 with the complaint as the "translation", don't write that into a subtitle
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

            // their own 0..1 match score, better than anything we could guess
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
}
