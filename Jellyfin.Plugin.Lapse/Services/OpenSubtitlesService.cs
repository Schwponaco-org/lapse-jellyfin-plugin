// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Fetches a subtitle from OpenSubtitles for an item that has none, so a sync has
/// something to work with. Experimental: it depends on a third party service, on an
/// account, and on their search finding the right file for a release it has only a
/// filename to go on.
///
/// Their v1 API splits this in two. Search takes the API key on its own. Download needs a
/// bearer token that only /login produces, and it comes out of a daily quota tied to the
/// account. That's why this asks for a username and password as well as a key - with only
/// a key it would find a subtitle and then fail at the last step, which is a worse
/// experience than saying so up front.
/// </summary>
public class OpenSubtitlesService : IDisposable
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenSubtitlesService> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;
    private DateTime _tokenExpiresUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public OpenSubtitlesService(IHttpClientFactory httpClientFactory, ILogger<OpenSubtitlesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the login lock.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loginLock.Dispose();
        }
    }

    /// <summary>
    /// Says why fetching isn't set up, or null when it is.
    /// </summary>
    /// <returns>A message for the dashboard, or null.</returns>
    public static string? GetConfigurationProblem()
    {
        var config = Plugin.Instance?.Configuration;

        if (config is null || !config.OpenSubtitlesEnabled)
        {
            return "Turned off.";
        }

        if (string.IsNullOrWhiteSpace(config.OpenSubtitlesApiKey))
        {
            return "No API key. Register an app at opensubtitles.com to get one.";
        }

        if (string.IsNullOrWhiteSpace(config.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(config.OpenSubtitlesPassword))
        {
            return "Searching works with just the API key, but downloading needs the account name and password too.";
        }

        return null;
    }

    /// <summary>
    /// Looks for a subtitle for an item and writes it next to the video file.
    /// </summary>
    /// <param name="item">The item with no subtitle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path of the file that was written, or null if nothing was found.</returns>
    public async Task<string?> TryFetchAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;

        if (GetConfigurationProblem() is { } problem)
        {
            _logger.LogInformation("Not fetching a subtitle from OpenSubtitles: {Problem}", problem);
            return null;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var language = config.OpenSubtitlesLanguage.Trim().ToLowerInvariant();

        try
        {
            var fileId = await SearchAsync(item, language, cancellationToken).ConfigureAwait(false);
            if (fileId is null)
            {
                _logger.LogInformation("OpenSubtitles had no {Language} subtitle for {Item}", language, item.Name);
                return null;
            }

            var link = await GetDownloadLinkAsync(fileId.Value, cancellationToken).ConfigureAwait(false);
            if (link is null)
            {
                return null;
            }

            return await SaveAsync(item.Path, language, link, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch a subtitle from OpenSubtitles for {Item}", item.Name);
            return null;
        }
    }

    private static string BuildSearchQuery(BaseItem item, string language)
    {
        var parts = new List<string>
        {
            "languages=" + Uri.EscapeDataString(language),
            "order_by=download_count",
            "order_direction=desc"
        };

        // Their matcher does best with the release file name, since that's what uploaders
        // name their subtitles after. Falling back to the title plus season and episode
        // covers the libraries that have been renamed into something tidier.
        var fileName = Path.GetFileNameWithoutExtension(item.Path);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            parts.Add("query=" + Uri.EscapeDataString(fileName));
        }

        if (item is Episode episode)
        {
            if (episode.ParentIndexNumber.HasValue)
            {
                parts.Add("season_number=" + episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (episode.IndexNumber.HasValue)
            {
                parts.Add("episode_number=" + episode.IndexNumber.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return "subtitles?" + string.Join('&', parts);
    }

    private async Task<long?> SearchAsync(BaseItem item, string language, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + BuildSearchQuery(item, language));
        AddApiKey(request);

        using var client = CreateClient();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenSubtitles search returned {Status}", response.StatusCode);
            return null;
        }

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
        if (document is null || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("attributes", out var attributes)
                || !attributes.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("file_id", out var fileId) && fileId.ValueKind == JsonValueKind.Number)
                {
                    return fileId.GetInt64();
                }
            }
        }

        return null;
    }

    private async Task<string?> GetDownloadLinkAsync(long fileId, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "download")
        {
            Content = JsonContent.Create(new { file_id = fileId })
        };

        AddApiKey(request);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var client = CreateClient();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "OpenSubtitles refused the download with {Status}. A 406 here means the account's daily quota is used up.",
                response.StatusCode);
            return null;
        }

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
        return document is not null && document.RootElement.TryGetProperty("link", out var link)
            ? link.GetString()
            : null;
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        // Their tokens last a day and logging in repeatedly is itself rate limited, so
        // hold onto one rather than getting a fresh one per subtitle.
        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc)
        {
            return _token;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc)
            {
                return _token;
            }

            var config = Plugin.Instance!.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "login")
            {
                Content = JsonContent.Create(new
                {
                    username = config.OpenSubtitlesUsername,
                    password = config.OpenSubtitlesPassword
                })
            };

            AddApiKey(request);

            using var client = CreateClient();
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not sign in to OpenSubtitles: {Status}", response.StatusCode);
                return null;
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            _token = document is not null && document.RootElement.TryGetProperty("token", out var token)
                ? token.GetString()
                : null;

            _tokenExpiresUtc = DateTime.UtcNow.AddHours(20);
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The destination is derived from the library item's own video path plus a language code the admin typed into the settings, not from anything the remote service sent back.")]
    private async Task<string> SaveAsync(string videoPath, string language, string link, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var destination = Path.Combine(folder, $"{stem}.{language}.srt");

        // Never write over a subtitle that is already there. This only runs when the item
        // had none, but two syncs racing on the same item shouldn't be able to clobber
        // what the first one fetched.
        var attempt = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(folder, $"{stem}.{language}.{attempt}.srt");
            attempt++;
        }

        using var client = CreateClient();
        var bytes = await client.GetByteArrayAsync(new Uri(link), cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Fetched a {Language} subtitle from OpenSubtitles to {Path}", language, destination);
        return destination;
    }

    private static void AddApiKey(HttpRequestMessage request)
    {
        request.Headers.Add("Api-Key", Plugin.Instance!.Configuration.OpenSubtitlesApiKey);
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("Lapse");
    }
}
