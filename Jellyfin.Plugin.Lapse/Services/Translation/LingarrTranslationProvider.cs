// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// Lingarr, self hosted. It exposes POST /api/translate/line, which takes one subtitle
/// line plus a bit of surrounding context and hands the translation straight back, so
/// this walks the file a line at a time. Slower than a batch API, but Lingarr's line
/// endpoint is the one that's stable across its versions, and giving it the neighbouring
/// lines as context measurably helps with dialogue that runs across cues.
/// </summary>
public class LingarrTranslationProvider : ITranslationProvider
{
    private const int ContextLines = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LingarrTranslationProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LingarrTranslationProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public LingarrTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<LingarrTranslationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public TranslationProvider Id => TranslationProvider.Lingarr;

    /// <inheritdoc />
    public string DisplayName => "Lingarr";

    /// <inheritdoc />
    public int Tier => 1;

    /// <inheritdoc />
    public string Summary => "Self hosted. Translates a line at a time with its neighbours as context, which helps with dialogue that runs across cues.";

    /// <inheritdoc />
    public string? GetConfigurationProblem()
    {
        var baseUrl = Plugin.Instance?.Configuration.LingarrBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "No Lingarr base URL is set. Add one in the LAPSE dashboard under Translation.";
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out _)
            ? null
            : $"'{baseUrl}' isn't a URL Lingarr can be reached at. It should look like http://lingarr:9876.";
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
        var url = config.LingarrBaseUrl!.TrimEnd('/') + "/api/translate/line";

        var client = _httpClientFactory.CreateClient("Lapse");
        var results = new List<TranslatedLine>(lines.Count);
        var source = ResolveSource(sourceLanguage, config);

        for (var i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = new
            {
                subtitleLine = lines[i],
                sourceLanguage = source,
                targetLanguage,
                contextLinesBefore = Context(lines, i - ContextLines, i),
                contextLinesAfter = Context(lines, i + 1, i + 1 + ContextLines)
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(config.LingarrApiKey))
            {
                request.Headers.Add("X-Api-Key", config.LingarrApiKey);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Lingarr returned {0} for {1}: {2}",
                    (int)response.StatusCode,
                    url,
                    Shorten(body)));
            }

            var translated = await ReadLineAsync(response, cancellationToken).ConfigureAwait(false);
            results.Add(new TranslatedLine { Text = translated });
        }

        return results;
    }

    private async Task<string?> ReadLineAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

        if (raw.Length == 0)
        {
            return null;
        }

        // the endpoint returns a bare JSON string, so it arrives quoted
        if (raw.StartsWith('"'))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Lingarr returned something that didn't parse as a JSON string: {Raw}", Shorten(raw));
            }
        }

        // Some builds wrap the line in an object instead. Take the text out of it rather
        // than dropping the whole JSON document into the subtitle, which is what
        // returning the body as-is would do.
        if (raw.StartsWith('{'))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(raw);

                foreach (var name in new[] { "translatedText", "translation", "text", "line", "result" })
                {
                    if (document.RootElement.TryGetProperty(name, out var value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Lingarr returned something that didn't parse as JSON: {Raw}", Shorten(raw));
            }

            // An object we can't read is not a translation, and a subtitle line is a
            // worse place for it than the log.
            _logger.LogWarning("Lingarr answered with JSON that has no line in it: {Raw}", Shorten(raw));
            return null;
        }

        return raw;
    }

    /// <summary>
    /// Lingarr validates both languages as .NET cultures and turns down anything that
    /// isn't one, so "auto" - which the rest of the plugin uses to mean "nobody said" -
    /// comes back as a 500 reading "all configured translation services were skipped, no
    /// service supports auto->xx". Send a real code instead: the configured default, or
    /// English, which is what the overwhelming majority of subtitles being translated
    /// away from are in.
    /// </summary>
    private static string ResolveSource(string? sourceLanguage, Configuration.PluginConfiguration config)
    {
        var trimmed = sourceLanguage?.Trim();

        if (!string.IsNullOrEmpty(trimmed) && !trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var configured = config.TranslationDefaultSourceLanguage?.Trim();

        return string.IsNullOrEmpty(configured) || configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : configured;
    }

    private static List<string> Context(IReadOnlyList<string> lines, int from, int to)
    {
        var result = new List<string>();
        for (var i = Math.Max(0, from); i < Math.Min(lines.Count, to); i++)
        {
            result.Add(lines[i]);
        }

        return result;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
