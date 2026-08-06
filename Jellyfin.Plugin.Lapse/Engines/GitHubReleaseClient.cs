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
/// Asks GitHub what the newest release of an engine is. Answers are cached for a while
/// because the API is rate limited fairly aggressively for unauthenticated callers and
/// the dashboard asks about every engine each time it loads.
/// </summary>
public class GitHubReleaseClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubReleaseClient> _logger;
    private readonly ConcurrentDictionary<string, (string Tag, DateTime FetchedUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public GitHubReleaseClient(IHttpClientFactory httpClientFactory, ILogger<GitHubReleaseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the tag of the newest release of a repo, e.g. "v1.0.7".
    /// </summary>
    /// <param name="repo">The repo as "owner/name".</param>
    /// <param name="force">True to ignore anything cached and ask again.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tag, or null if GitHub couldn't be reached or the repo has no releases.</returns>
    public async Task<string?> GetLatestTagAsync(string? repo, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        if (!force && _cache.TryGetValue(repo, out var cached) && DateTime.UtcNow - cached.FetchedUtc < CacheFor)
        {
            return cached.Tag;
        }

        var url = string.Format(CultureInfo.InvariantCulture, "https://api.github.com/repos/{0}/releases/latest", repo);

        try
        {
            var client = _httpClientFactory.CreateClient("Lapse");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GitHub returned {Status} asking about the latest release of {Repo}", response.StatusCode, repo);
                return null;
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            if (document is null || !document.RootElement.TryGetProperty("tag_name", out var tagElement))
            {
                return null;
            }

            var tag = tagElement.GetString();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _cache[repo] = (tag, DateTime.UtcNow);
            }

            return tag;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Could not ask GitHub about the latest release of {Repo}", repo);
            return null;
        }
    }

    /// <summary>
    /// Compares two release tags well enough to answer "is there something newer than
    /// what I have". Handles the usual "v1.2.3" shape and falls back to a plain string
    /// comparison for anything that isn't a dotted version.
    /// </summary>
    /// <param name="installed">The tag currently installed, if any.</param>
    /// <param name="latest">The newest tag published.</param>
    /// <returns>True if latest is a newer release than installed.</returns>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        // never installed through the plugin, so there's nothing to compare and nothing
        // we can honestly call an update
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
