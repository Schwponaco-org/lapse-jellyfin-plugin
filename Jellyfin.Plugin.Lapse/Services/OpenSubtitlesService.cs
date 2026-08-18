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
/// What came of trying to fetch a subtitle. Every way this can fail used to end in a log
/// line and a null, which from the dashboard looked exactly like nothing having happened
/// at all, so the reason comes back with it now.
/// </summary>
public class SubtitleFetchResult
{
    /// <summary>
    /// Gets or sets the file that was written, or null when nothing was.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets which step this got to: search, login, download or save. Null when it
    /// never started, and null when it worked.
    /// </summary>
    public string? FailedStep { get; set; }

    /// <summary>
    /// Gets or sets what went wrong, in words meant for whoever pressed Sync.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets a value indicating whether a subtitle was actually fetched.
    /// </summary>
    public bool Success => Path is not null;

    /// <summary>
    /// Builds a failed result.
    /// </summary>
    /// <param name="step">Which step failed.</param>
    /// <param name="error">Why.</param>
    /// <returns>The result.</returns>
    public static SubtitleFetchResult Failed(string step, string error)
    {
        return new SubtitleFetchResult { FailedStep = step, Error = error };
    }
}

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
public partial class OpenSubtitlesService : IDisposable
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\{\d+\}\{\d+\}")]
    private static partial System.Text.RegularExpressions.Regex MicroDvdRegex();

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
    /// <returns>What was fetched, or why nothing was.</returns>
    public async Task<SubtitleFetchResult> TryFetchAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;

        if (GetConfigurationProblem() is { } problem)
        {
            _logger.LogInformation("Not fetching a subtitle from OpenSubtitles: {Problem}", problem);
            return SubtitleFetchResult.Failed("setup", "OpenSubtitles isn't set up: " + problem);
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return SubtitleFetchResult.Failed("setup", "That item has no video file to put a subtitle next to.");
        }

        var language = config.OpenSubtitlesLanguage.Trim().ToLowerInvariant();

        try
        {
            var found = await SearchAsync(item, language, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                _logger.LogWarning("OpenSubtitles had no {Language} subtitle for {Item}", language, item.Name);
                return SubtitleFetchResult.Failed(
                    "search",
                    $"OpenSubtitles has no {language} subtitle for {item.Name}.");
            }

            var download = await GetDownloadAsync(found.Value.FileId, cancellationToken).ConfigureAwait(false);
            if (download.Link is null)
            {
                return SubtitleFetchResult.Failed(download.FailedStep ?? "download", download.Error ?? "The download was refused.");
            }

            // Their download response names the file, and that name is the only reliable
            // word on what format it actually is. Falling back to the name the search
            // gave, then to srt, which is what most of them are.
            var fileName = download.FileName ?? found.Value.FileName;

            return await SaveAsync(item.Path, language, download.Link, fileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch a subtitle from OpenSubtitles for {Item}", item.Name);
            return SubtitleFetchResult.Failed("network", "Could not reach OpenSubtitles: " + ex.Message);
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

    private async Task<(long FileId, string? FileName)?> SearchAsync(BaseItem item, string language, CancellationToken cancellationToken)
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
                    var name = file.TryGetProperty("file_name", out var fileName) && fileName.ValueKind == JsonValueKind.String
                        ? fileName.GetString()
                        : null;

                    return (fileId.GetInt64(), name);
                }
            }
        }

        return null;
    }

    private async Task<(string? Link, string? FileName, string? FailedStep, string? Error)> GetDownloadAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return (null, null, "login", "Could not sign in to OpenSubtitles. Check the account name and password in the LAPSE settings.");
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

            var reason = response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotAcceptable => "the account's daily download quota is used up",
                System.Net.HttpStatusCode.Unauthorized => "the login was rejected - check the account name and password",
                System.Net.HttpStatusCode.TooManyRequests => "OpenSubtitles is rate limiting the account, try again shortly",
                _ => "it answered " + (int)response.StatusCode
            };

            // A rejected token is worth throwing away, so the next attempt logs in again
            // rather than reusing something the server has stopped accepting.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _token = null;
            }

            return (null, null, "download", "OpenSubtitles refused the download: " + reason + ".");
        }

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);

        if (document is null || !document.RootElement.TryGetProperty("link", out var link) || link.GetString() is not { } url)
        {
            return (null, null, "download", "OpenSubtitles accepted the request but didn't send back a download link.");
        }

        var name = document.RootElement.TryGetProperty("file_name", out var fileName) && fileName.ValueKind == JsonValueKind.String
            ? fileName.GetString()
            : null;

        return (url, name, null, null);
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
        Justification = "The destination is derived from the library item's own video path plus a language code the admin typed into the settings, and an extension picked from a fixed list, not from anything the remote service sent back.")]
    private async Task<SubtitleFetchResult> SaveAsync(
        string videoPath,
        string language,
        string link,
        string? remoteFileName,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        byte[] bytes;
        try
        {
            bytes = await client.GetByteArrayAsync(new Uri(link), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "The OpenSubtitles download link didn't work");
            return SubtitleFetchResult.Failed("download", "The OpenSubtitles download link didn't work: " + ex.Message);
        }

        // Their download links hand back an error page rather than a file when the
        // account is being rate limited, and writing that to disk as a .srt used to leave
        // an item looking like it had a subtitle that no engine could read.
        if (DescribeBadPayload(bytes) is { } badPayload)
        {
            _logger.LogWarning("OpenSubtitles sent back something that isn't a subtitle: {Problem}", badPayload);
            return SubtitleFetchResult.Failed("download", "OpenSubtitles sent back " + badPayload);
        }

        var extension = ResolveExtension(remoteFileName);
        var folder = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var destination = Path.Combine(folder, $"{stem}.{language}{extension}");

        // Never write over a subtitle that is already there. This only runs when the item
        // had none, but two syncs racing on the same item shouldn't be able to clobber
        // what the first one fetched.
        var attempt = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(folder, $"{stem}.{language}.{attempt.ToString(CultureInfo.InvariantCulture)}{extension}");
            attempt++;
        }

        await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Fetched a {Language} subtitle from OpenSubtitles to {Path}", language, destination);
        return new SubtitleFetchResult { Path = destination };
    }

    // Says what's wrong with the downloaded bytes, or null if they look like a subtitle.
    // Deliberately loose: the point is to catch an html error page or an empty response,
    // not to validate someone's subtitle file for them.
    private static string? DescribeBadPayload(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "an empty file.";
        }

        // Long enough to hold a cue, short enough that a truncated download is caught.
        if (bytes.Length < 32)
        {
            return "a file too small to be a subtitle.";
        }

        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).TrimStart('\uFEFF', ' ', '\n', '\r', '\t');

        if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return "a web page instead of a subtitle file - usually what their rate limiting looks like.";
        }

        // A MicroDVD file opens with {frame}{frame}, which is the one subtitle format that
        // starts the same way an error payload does.
        if (MicroDvdRegex().IsMatch(head))
        {
            return null;
        }

        if (head.StartsWith('{') || head.StartsWith('['))
        {
            return "an error message instead of a subtitle file: " + Shorten(head);
        }

        var looksLikeSubtitle = head.Contains("-->", StringComparison.Ordinal)
            || head.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase)
            || head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
            || head.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase);

        return looksLikeSubtitle ? null : "a file with no subtitle cues in it.";
    }

    private static string Shorten(string text)
    {
        var single = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length > 120 ? single[..120] + "..." : single;
    }

    // Takes the extension off the name OpenSubtitles gave the file, as long as it's a
    // subtitle format the plugin recognises. Their downloads are usually srt but not
    // always, and saving an ass file as .srt used to break the sync that ran next.
    private static string ResolveExtension(string? remoteFileName)
    {
        if (string.IsNullOrWhiteSpace(remoteFileName))
        {
            return ".srt";
        }

        var extension = Path.GetExtension(remoteFileName);
        return SubtitleFormats.IsSubtitle(extension) ? extension.ToLowerInvariant() : ".srt";
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
