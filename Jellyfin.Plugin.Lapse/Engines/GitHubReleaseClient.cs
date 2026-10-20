// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Asks GitHub for an engine's newest release. Cached since the API is rate limited for
/// anonymous callers and the dashboard asks about every engine on every load.
/// </summary>
public class GitHubReleaseClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    // Failures get remembered too, briefly. Otherwise an offline server (or one that hit the
    // rate limit) retried every engine on every page load and the engine page just hung.
    private static readonly TimeSpan FailureCacheFor = TimeSpan.FromMinutes(5);

    // HttpClient's default is 100s, way too long for something the dashboard waits on
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubReleaseClient> _logger;
    private readonly ConcurrentDictionary<string, (string? Tag, DateTime FetchedUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseClient"/> class.
    /// </summary>
    public GitHubReleaseClient(IHttpClientFactory httpClientFactory, ILogger<GitHubReleaseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the tag of the newest release of a repo, e.g. "v1.0.7".
    /// </summary>
    public async Task<string?> GetLatestTagAsync(string? repo, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        if (!force
            && _cache.TryGetValue(repo, out var cached)
            && DateTime.UtcNow - cached.FetchedUtc < (cached.Tag is null ? FailureCacheFor : CacheFor))
        {
            return cached.Tag;
        }

        var url = string.Format(CultureInfo.InvariantCulture, "https://api.github.com/repos/{0}/releases/latest", repo);
        string? tag = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var client = _httpClientFactory.CreateClient("Lapse");
            using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(timeout.Token).ConfigureAwait(false);
                if (document is not null && document.RootElement.TryGetProperty("tag_name", out var tagElement))
                {
                    tag = tagElement.GetString();
                }
            }
            else
            {
                _logger.LogDebug("GitHub returned {Status} asking about the latest release of {Repo}", response.StatusCode, repo);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not ask GitHub about the latest release of {Repo}", repo);
        }

        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(tag))
        {
            _cache[repo] = (tag, now);
            return tag;
        }

        // failed: keep a tag we already knew for a few more minutes, or remember the failure
        var previous = _cache.TryGetValue(repo, out var old) ? old.Tag : null;
        _cache[repo] = previous is null ? (null, now) : (previous, now - CacheFor + FailureCacheFor);

        // a forced check wants a real answer, so say we don't know rather than hand back the old one
        return force ? null : previous;
    }

    /// <summary>
    /// Says whether latest is newer than installed. Handles "v1.2.3", falls back to "are they different".
    /// </summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        // never installed through us, nothing to compare
        if (string.IsNullOrWhiteSpace(installed))
        {
            return false;
        }

        if (TryParseVersion(installed, out var installedVersion) && TryParseVersion(latest, out var latestVersion))
        {
            return latestVersion > installedVersion;
        }

        return !string.Equals(installed.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var trimmed = tag.Trim().TrimStart('v', 'V');

        // strip anything after a prerelease marker, Version can't parse "1.2.3-beta1"
        var dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            trimmed = trimmed[..dashIndex];
        }

        return Version.TryParse(trimmed, out version!);
    }
}
