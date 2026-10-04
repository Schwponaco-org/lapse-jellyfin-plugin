// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
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
    /// Gets or sets which step this got to: setup, search, login, download or save. Null
    /// when it worked.
    /// </summary>
    public string? FailedStep { get; set; }

    /// <summary>
    /// Gets or sets what went wrong, in words meant for whoever pressed the button.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets how many downloads the account has left today, when OpenSubtitles
    /// said.
    /// </summary>
    public int? RemainingDownloads { get; set; }

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
/// Fetches subtitles from OpenSubtitles for items that have none, or none in the language
/// someone wants. Experimental: it depends on a third party service and an account there.
///
/// Their REST API (api.opensubtitles.com, v1) has a few rules that are easy to trip over,
/// and the first version of this tripped over most of them:
///
/// - Every request needs an Api-Key and a User-Agent naming the app.
/// - Search parameters have to be in alphabetical order, in lower case, with spaces as
///   "+". Anything else is answered with a redirect to the tidy version of the URL.
/// - Downloading needs a token from /login, comes out of a daily quota tied to the
///   account, and has to go to whichever host /login names, which differs for VIP
///   accounts.
///
/// Finding the right subtitle goes, in order: the file's own hash (an exact match for the
/// release), the IMDb or TMDb id Jellyfin already has for the film or show, the title, and
/// last the file name. The result is synced afterwards anyway, so a subtitle for another
/// release of the same film is fine; a subtitle for another film is not, which is why the
/// ids come before any text search.
/// </summary>
public partial class OpenSubtitlesService : IDisposable
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\{\d+\}\{\d+\}")]
    private static partial System.Text.RegularExpressions.Regex MicroDvdRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z]{2,3}(-[a-z]{2,4})?$")]
    private static partial System.Text.RegularExpressions.Regex LanguageCodeRegex();

    private const string DefaultHost = "api.opensubtitles.com";
    private const string ApiPath = "/api/v1/";

    // OpenSubtitles hashes the file size plus the first and last 64KB, read as 64-bit
    // little endian words. Cheap even over a network share.
    private const int HashChunk = 64 * 1024;

    // Enough to pick from without scrolling forever on a TV remote.
    private const int MaxCandidates = 25;

    private static readonly string Version =
        typeof(OpenSubtitlesService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SubtitleLanguages _languages;
    private readonly ILogger<OpenSubtitlesService> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;
    private string? _tokenOwner;
    private string _downloadHost = DefaultHost;
    private DateTime _tokenExpiresUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="languages">Turns typed languages into OpenSubtitles' codes.</param>
    /// <param name="logger">Logger.</param>
    public OpenSubtitlesService(IHttpClientFactory httpClientFactory, SubtitleLanguages languages, ILogger<OpenSubtitlesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _languages = languages;
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
    /// Computes the OpenSubtitles hash of a video file: its size plus the sum of its first
    /// and last 64KB as 64-bit little endian words, in hex.
    /// </summary>
    /// <param name="path">The video.</param>
    /// <returns>The hash, or null for a file too small to have one or one that can't be read.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path is Jellyfin's own path for a library item.")]
    public static string? ComputeMovieHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            var size = stream.Length;

            if (size < HashChunk * 2)
            {
                return null;
            }

            var buffer = new byte[HashChunk];
            var hash = unchecked((ulong)size);

            stream.ReadExactly(buffer);
            hash = unchecked(hash + SumWords(buffer));

            stream.Seek(size - HashChunk, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            hash = unchecked(hash + SumWords(buffer));

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a search query the way the API wants it: parameters in alphabetical order,
    /// values in lower case, spaces as "+". Anything else costs a redirect per request.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The query string, without the question mark.</returns>
    public static string BuildQuery(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join(
            '&',
            parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value.Trim().ToLowerInvariant()).Replace("%20", "+", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Works out which languages to search for, in the order they're preferred. The
    /// settings box takes several - "da, en" means Danish if there is one, English
    /// otherwise - and any way of writing them.
    /// </summary>
    /// <param name="typed">What's in the settings, or what a dialog asked for.</param>
    /// <returns>OpenSubtitles language codes, best first.</returns>
    public List<string> ResolveLanguages(string? typed)
    {
        var codes = new List<string>();

        foreach (var entry in SubtitleLanguages.SplitList(typed))
        {
            if (_languages.ToOpenSubtitlesCode(entry) is { } code && !codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>
    /// Finds the best subtitle for an item in the configured languages and writes it next
    /// to the video. What a sync uses when the item has nothing to sync.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was fetched, or why nothing was.</returns>
    public async Task<SubtitleFetchResult> TryFetchAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        if (GetConfigurationProblem() is { } problem)
        {
            _logger.LogInformation("Not fetching a subtitle from OpenSubtitles: {Problem}", problem);
            return SubtitleFetchResult.Failed("setup", "OpenSubtitles isn't set up: " + problem);
        }

        var search = await SearchAsync(item, null, cancellationToken).ConfigureAwait(false);
        if (search.Error is not null)
        {
            return SubtitleFetchResult.Failed("search", search.Error);
        }

        if (search.Candidates.Count == 0)
        {
            var languages = string.Join(" or ", ResolveLanguages(Plugin.Instance!.Configuration.OpenSubtitlesLanguage));
            _logger.LogInformation("OpenSubtitles had no {Languages} subtitle for {Item}", languages, item.Name);
            return SubtitleFetchResult.Failed("search", $"OpenSubtitles has no {languages} subtitle for {item.Name}.");
        }

        var best = search.Candidates[0];
        _logger.LogInformation(
            "Fetching {Release} ({Language}) for {Item}, found by {SearchedBy}",
            best.Release ?? best.FileName,
            best.Language,
            item.Name,
            search.SearchedBy);

        return await DownloadAsync(item, best, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for an item's subtitles: by the file's hash and the item's ids first, then
    /// by title, then by file name, stopping at the first that finds anything.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="languageOverride">Languages to search instead of the configured ones,
    /// or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found, best first.</returns>
    public async Task<OpenSubtitlesSearchResult> SearchAsync(BaseItem item, string? languageOverride, CancellationToken cancellationToken = default)
    {
        var result = new OpenSubtitlesSearchResult();
        var config = Plugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(config.OpenSubtitlesApiKey))
        {
            result.Error = "OpenSubtitles needs an API key, set under Settings, Experimental.";
            return result;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            result.Error = "That item has no video file to put a subtitle next to.";
            return result;
        }

        var languages = ResolveLanguages(string.IsNullOrWhiteSpace(languageOverride) ? config.OpenSubtitlesLanguage : languageOverride);
        if (languages.Count == 0)
        {
            result.Error = "No language to search for. Put one under Settings, Experimental, for example en or da, en.";
            return result;
        }

        var ordered = new List<string>(languages);
        var languageList = string.Join(',', languages.OrderBy(l => l, StringComparer.Ordinal));
        var hash = ComputeMovieHash(item.Path);

        try
        {
            foreach (var (by, parameters) in BuildSearches(item, hash))
            {
                parameters["languages"] = languageList;

                var (found, error) = await RunSearchAsync(parameters, hash, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    result.Error = error;
                    return result;
                }

                if (found.Count == 0)
                {
                    continue;
                }

                result.SearchedBy = by;
                result.Candidates.AddRange(Rank(found, ordered).Take(MaxCandidates));
                return result;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Could not search OpenSubtitles for {Item}", item.Name);
            result.Error = "Could not reach OpenSubtitles: " + ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Downloads one subtitle for an item and writes it next to the video.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="candidate">The subtitle, from a search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was written, or why nothing was.</returns>
    public async Task<SubtitleFetchResult> DownloadAsync(BaseItem item, OpenSubtitlesCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (GetConfigurationProblem() is { } problem)
        {
            return SubtitleFetchResult.Failed("setup", "OpenSubtitles isn't set up: " + problem);
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return SubtitleFetchResult.Failed("setup", "That item has no video file to put a subtitle next to.");
        }

        // The language ends up in a file name, so only something shaped like a language
        // code gets that far.
        var language = (candidate.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (!LanguageCodeRegex().IsMatch(language))
        {
            language = ResolveLanguages(Plugin.Instance!.Configuration.OpenSubtitlesLanguage).FirstOrDefault() ?? "und";
        }

        try
        {
            var (token, loginError) = await GetTokenAsync(ReadCredentials(null), cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return SubtitleFetchResult.Failed("login", loginError ?? "Could not sign in to OpenSubtitles.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + _downloadHost + ApiPath + "download")
            {
                Content = JsonContent.Create(new { file_id = candidate.FileId })
            };

            Prepare(request, ReadCredentials(null).ApiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var client = CreateClient();
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized)
                {
                    // Thrown away so the next attempt signs in again rather than reusing a
                    // token the server has stopped taking.
                    _token = null;
                }

                var reason = DescribeFailure(response.StatusCode, body, "download");
                _logger.LogWarning("OpenSubtitles refused the download of {FileId}: {Reason}", candidate.FileId, reason);
                return SubtitleFetchResult.Failed("download", reason);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (ReadString(root, "link") is not { } link)
            {
                return SubtitleFetchResult.Failed("download", "OpenSubtitles took the request but sent no download link back.");
            }

            // Their download response names the file, and that name is the only reliable
            // word on what format it actually is.
            var fileName = ReadString(root, "file_name") ?? candidate.FileName;
            var remaining = ReadInt(root, "remaining");

            var saved = await SaveAsync(item.Path, language, candidate, link, fileName, cancellationToken).ConfigureAwait(false);
            saved.RemainingDownloads = remaining;
            return saved;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Could not fetch a subtitle from OpenSubtitles for {Item}", item.Name);
            return SubtitleFetchResult.Failed("network", "Could not reach OpenSubtitles: " + ex.Message);
        }
    }

    /// <summary>
    /// Checks the key and the account, for the settings page: signs in and asks how many
    /// downloads are left today.
    /// </summary>
    /// <param name="request">Credentials to test instead of the saved ones, where filled in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How it went.</returns>
    public async Task<OpenSubtitlesAccountStatus> TestAsync(OpenSubtitlesTestRequest? request, CancellationToken cancellationToken = default)
    {
        var credentials = ReadCredentials(request);

        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return new OpenSubtitlesAccountStatus { Message = "There's no API key to test." };
        }

        try
        {
            if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
            {
                // No account, so all there is to test is that the key is taken.
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["languages"] = "en",
                    ["query"] = "test"
                };

                using var searchRequest = new HttpRequestMessage(HttpMethod.Get, "https://" + DefaultHost + ApiPath + "subtitles?" + BuildQuery(parameters));
                Prepare(searchRequest, credentials.ApiKey);

                using var client = CreateClient();
                using var searchResponse = await client.SendAsync(searchRequest, cancellationToken).ConfigureAwait(false);
                var searchBody = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return searchResponse.IsSuccessStatusCode
                    ? new OpenSubtitlesAccountStatus { Message = "The API key works, so searching does. Downloading needs the account name and password as well." }
                    : new OpenSubtitlesAccountStatus { Message = DescribeFailure(searchResponse.StatusCode, searchBody, "search") };
            }

            var (token, loginError) = await GetTokenAsync(credentials, cancellationToken, force: true).ConfigureAwait(false);
            if (token is null)
            {
                return new OpenSubtitlesAccountStatus { Message = loginError ?? "Could not sign in." };
            }

            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://" + _downloadHost + ApiPath + "infos/user");
            Prepare(userRequest, credentials.ApiKey);
            userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var userClient = CreateClient();
            using var userResponse = await userClient.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
            var userBody = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!userResponse.IsSuccessStatusCode)
            {
                return new OpenSubtitlesAccountStatus
                {
                    Success = true,
                    Message = "Signed in, but OpenSubtitles wouldn't say how many downloads are left: "
                        + DescribeFailure(userResponse.StatusCode, userBody, "account")
                };
            }

            using var document = JsonDocument.Parse(userBody);
            var data = document.RootElement.TryGetProperty("data", out var inner) ? inner : document.RootElement;

            var status = new OpenSubtitlesAccountStatus
            {
                Success = true,
                RemainingDownloads = ReadInt(data, "remaining_downloads"),
                AllowedDownloads = ReadInt(data, "allowed_downloads")
            };

            var level = ReadString(data, "level");
            status.Message = "Signed in"
                + (string.IsNullOrWhiteSpace(level) ? string.Empty : " (" + level + ")")
                + (status.RemainingDownloads is { } left
                    ? string.Create(CultureInfo.InvariantCulture, $". {left} of {status.AllowedDownloads?.ToString(CultureInfo.InvariantCulture) ?? "?"} downloads left today.")
                    : ".");

            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new OpenSubtitlesAccountStatus { Message = "Could not reach OpenSubtitles: " + ex.Message };
        }
    }

    // The searches to try, best first. Each one is only there when the item has what it
    // needs: no hash for a tiny file, no ids for a home video.
    private static List<(string By, Dictionary<string, string> Parameters)> BuildSearches(BaseItem item, string? hash)
    {
        var searches = new List<(string, Dictionary<string, string>)>();
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var by = new List<string>();

        if (hash is not null)
        {
            exact["moviehash"] = hash;
            by.Add("the file's hash");
        }

        if (item is Episode episode)
        {
            var series = episode.Series;
            var imdb = CleanImdb(episode.GetProviderId(MetadataProvider.Imdb));
            var tmdb = CleanNumber(episode.GetProviderId(MetadataProvider.Tmdb));
            var parentImdb = CleanImdb(series?.GetProviderId(MetadataProvider.Imdb));
            var parentTmdb = CleanNumber(series?.GetProviderId(MetadataProvider.Tmdb));

            // An episode's own id names exactly that episode. Failing that, the show's id
            // with the season and episode numbers does the same job.
            if (imdb is not null)
            {
                exact["imdb_id"] = imdb;
                by.Add("the episode's IMDb id");
            }
            else if (tmdb is not null)
            {
                exact["tmdb_id"] = tmdb;
                by.Add("the episode's TMDb id");
            }
            else if ((parentImdb ?? parentTmdb) is not null && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                if (parentImdb is not null)
                {
                    exact["parent_imdb_id"] = parentImdb;
                }
                else
                {
                    exact["parent_tmdb_id"] = parentTmdb!;
                }

                exact["season_number"] = episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture);
                exact["episode_number"] = episode.IndexNumber.Value.ToString(CultureInfo.InvariantCulture);
                by.Add("the show's id with the season and episode");
            }
        }
        else
        {
            var imdb = CleanImdb(item.GetProviderId(MetadataProvider.Imdb));
            var tmdb = CleanNumber(item.GetProviderId(MetadataProvider.Tmdb));

            if (imdb is not null)
            {
                exact["imdb_id"] = imdb;
                by.Add("its IMDb id");
            }
            else if (tmdb is not null)
            {
                exact["tmdb_id"] = tmdb;
                by.Add("its TMDb id");
            }
        }

        if (exact.Count > 0)
        {
            searches.Add((string.Join(" and ", by), exact));
        }

        // By title. For an episode that's the show's name plus the numbers.
        if (item is Episode titled && !string.IsNullOrWhiteSpace(titled.SeriesName)
            && titled.ParentIndexNumber.HasValue && titled.IndexNumber.HasValue)
        {
            searches.Add(("the show's name", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = titled.SeriesName,
                ["season_number"] = titled.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture),
                ["episode_number"] = titled.IndexNumber.Value.ToString(CultureInfo.InvariantCulture)
            }));
        }
        else if (item is not Episode && !string.IsNullOrWhiteSpace(item.Name))
        {
            var byTitle = new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = item.Name };
            if (item.ProductionYear is { } year)
            {
                byTitle["year"] = year.ToString(CultureInfo.InvariantCulture);
            }

            searches.Add(("its title", byTitle));
        }

        // Last, the release name, which is what uploaders name their files after.
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            searches.Add(("the file name", new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = stem }));
        }

        return searches;
    }

    private async Task<(List<OpenSubtitlesCandidate> Found, string? Error)> RunSearchAsync(
        Dictionary<string, string> parameters,
        string? hash,
        CancellationToken cancellationToken)
    {
        var found = new List<OpenSubtitlesCandidate>();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + DefaultHost + ApiPath + "subtitles?" + BuildQuery(parameters));
        Prepare(request, ReadCredentials(null).ApiKey);

        using var client = CreateClient();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var reason = DescribeFailure(response.StatusCode, body, "search");
            _logger.LogWarning("OpenSubtitles search failed: {Reason}", reason);
            return (found, reason);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return (found, null);
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("attributes", out var attributes)
                || !attributes.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // A subtitle split over two CDs comes as two files, one per half of an old
            // two disc rip. There's no putting those together for a single video file.
            if (files.GetArrayLength() != 1)
            {
                continue;
            }

            var file = files[0];
            if (!file.TryGetProperty("file_id", out var fileId) || fileId.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var title = attributes.TryGetProperty("feature_details", out var feature)
                ? ReadString(feature, "movie_name") ?? ReadString(feature, "title")
                : null;

            found.Add(new OpenSubtitlesCandidate
            {
                FileId = fileId.GetInt64(),
                FileName = ReadString(file, "file_name"),
                Language = (ReadString(attributes, "language") ?? string.Empty).ToLowerInvariant(),
                Release = ReadString(attributes, "release"),
                DownloadCount = ReadInt(attributes, "download_count") ?? 0,
                HashMatch = hash is not null && ReadBool(attributes, "moviehash_match"),
                FromTrusted = ReadBool(attributes, "from_trusted"),
                HearingImpaired = ReadBool(attributes, "hearing_impaired"),
                ForeignPartsOnly = ReadBool(attributes, "foreign_parts_only"),
                Translated = ReadBool(attributes, "ai_translated") || ReadBool(attributes, "machine_translated"),
                Title = title
            });
        }

        return (found, null);
    }

    // Best first: the preferred language, then a subtitle a person wrote over a machine
    // translation, then one made for this exact file, then full subtitles over forced only
    // ones, then trusted uploaders, then the most downloaded. Language leads because the
    // result gets synced anyway, so a perfect match in a language nobody asked for is the
    // worse outcome.
    private static IEnumerable<OpenSubtitlesCandidate> Rank(List<OpenSubtitlesCandidate> found, List<string> languages)
    {
        int LanguageRank(OpenSubtitlesCandidate c)
        {
            var index = languages.IndexOf(c.Language);
            return index < 0 ? languages.Count : index;
        }

        return found
            .OrderBy(LanguageRank)
            .ThenBy(c => c.Translated)
            .ThenByDescending(c => c.HashMatch)
            .ThenBy(c => c.ForeignPartsOnly)
            .ThenByDescending(c => c.FromTrusted)
            .ThenByDescending(c => c.DownloadCount);
    }

    private async Task<(string? Token, string? Error)> GetTokenAsync(
        (string? ApiKey, string? Username, string? Password) credentials,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
        {
            return (null, "Downloading needs the OpenSubtitles account name and password, set under Settings, Experimental.");
        }

        // A token belongs to one account. Changing the account in the settings must not
        // carry on downloading on the old one's quota.
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey + "\n" + credentials.Username + "\n" + credentials.Password)));

        if (!force && _token is not null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpiresUtc)
        {
            return (_token, null);
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!force && _token is not null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpiresUtc)
            {
                return (_token, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + DefaultHost + ApiPath + "login")
            {
                Content = JsonContent.Create(new { username = credentials.Username, password = credentials.Password })
            };

            Prepare(request, credentials.ApiKey);

            using var client = CreateClient();
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = DescribeFailure(response.StatusCode, body, "login");
                _logger.LogWarning("Could not sign in to OpenSubtitles: {Reason}", reason);
                return (null, reason);
            }

            using var document = JsonDocument.Parse(body);
            var token = ReadString(document.RootElement, "token");

            if (string.IsNullOrEmpty(token))
            {
                return (null, "OpenSubtitles took the sign in but sent no token back.");
            }

            // VIP accounts download from a host of their own, and /login is what says
            // which. Anything that isn't a plain host name is ignored rather than trusted.
            var host = ReadString(document.RootElement, "base_url")?.Trim().TrimEnd('/');
            if (host is not null && host.Contains("://", StringComparison.Ordinal))
            {
                host = host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..];
            }

            _downloadHost = host is not null && host.EndsWith("opensubtitles.com", StringComparison.OrdinalIgnoreCase) && !host.Contains('/', StringComparison.Ordinal)
                ? host
                : DefaultHost;

            _token = token;
            _tokenOwner = owner;

            // Tokens last a day, and signing in is itself rate limited, so one is kept
            // for most of that rather than fetched per subtitle.
            _tokenExpiresUtc = DateTime.UtcNow.AddHours(20);
            return (_token, null);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The destination is built from the library item's own video path, a language code checked against a strict pattern, and an extension picked from a fixed list. Nothing the remote service sent back reaches the path.")]
    private async Task<SubtitleFetchResult> SaveAsync(
        string videoPath,
        string language,
        OpenSubtitlesCandidate candidate,
        string link,
        string? remoteFileName,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return SubtitleFetchResult.Failed("download", "OpenSubtitles sent back a download link that doesn't look right, so it wasn't followed.");
        }

        using var client = CreateClient();

        byte[] bytes;
        try
        {
            bytes = await client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
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

        // Named the way Jellyfin reads a subtitle's flags off its file name, so a hearing
        // impaired or forced subtitle shows up as one in the player.
        var flags = (candidate.ForeignPartsOnly ? ".forced" : string.Empty) + (candidate.HearingImpaired ? ".sdh" : string.Empty);
        var destination = Path.Combine(folder, $"{stem}.{language}{flags}{extension}");

        // Never write over a subtitle that is already there.
        for (var attempt = 1; File.Exists(destination); attempt++)
        {
            destination = Path.Combine(folder, $"{stem}.{language}{flags}.{attempt.ToString(CultureInfo.InvariantCulture)}{extension}");
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

        var head = SubtitleEncoding.Decode(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).ToArray())
            .TrimStart('﻿', ' ', '\n', '\r', '\t');

        if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return "a web page instead of a subtitle file. That's usually what their rate limiting looks like.";
        }

        // A MicroDVD file opens with {frame}{frame}, which is the one subtitle format that
        // starts the same way an error payload does.
        if (MicroDvdRegex().IsMatch(head))
        {
            return null;
        }

        if (head.StartsWith('{') || head.StartsWith('['))
        {
            // ass files open with [Script Info].
            if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "an error message instead of a subtitle file: " + Shorten(head);
        }

        var looksLikeSubtitle = head.Contains("-->", StringComparison.Ordinal)
            || head.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase)
            || head.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<SAMI", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<tt", StringComparison.OrdinalIgnoreCase)
            || head.Contains("[INFORMATION]", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(head, @"^\[\d+\]\[\d+\]", System.Text.RegularExpressions.RegexOptions.Multiline);

        return looksLikeSubtitle ? null : "a file with no subtitle cues in it.";
    }

    // Turns an error response into a sentence, using what OpenSubtitles said about it when
    // it said anything.
    private static string DescribeFailure(HttpStatusCode status, string body, string step)
    {
        string? said = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            said = ReadString(document.RootElement, "message") ?? ReadString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            // An html error page, most likely. The status says enough.
        }

        var reason = status switch
        {
            HttpStatusCode.Unauthorized when step == "login" => "OpenSubtitles turned the account name or password down.",
            HttpStatusCode.Unauthorized => "the OpenSubtitles sign in has expired. Try again.",
            HttpStatusCode.Forbidden when said?.Contains("agent", StringComparison.OrdinalIgnoreCase) == true =>
                "OpenSubtitles turned the request down for its User-Agent.",
            HttpStatusCode.Forbidden => "OpenSubtitles turned the API key down. Check it under Settings, Experimental.",
            HttpStatusCode.NotAcceptable => "the account's downloads for today are used up.",
            HttpStatusCode.Gone => "that subtitle has been removed from OpenSubtitles.",
            HttpStatusCode.TooManyRequests => "OpenSubtitles is rate limiting the account. Wait a little and try again.",
            >= HttpStatusCode.InternalServerError => string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles is having trouble ({(int)status}). Try again later."),
            _ => string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles answered {(int)status}.")
        };

        reason = char.ToUpperInvariant(reason[0]) + reason[1..];

        return string.IsNullOrWhiteSpace(said) || reason.Contains(said, StringComparison.OrdinalIgnoreCase)
            ? reason
            : reason + " They said: " + Shorten(said);
    }

    private static string Shorten(string text)
    {
        var single = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length > 160 ? single[..160] + "..." : single;
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

    // tt0903747, 0903747 and 903747 are all the same film, and the API only takes the
    // last one without a redirect.
    private static string? CleanImdb(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var digits = id.Trim();
        if (digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return CleanNumber(digits);
    }

    private static string? CleanNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return null;
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static ulong SumWords(byte[] buffer)
    {
        ulong sum = 0;

        for (var i = 0; i + 8 <= buffer.Length; i += 8)
        {
            sum = unchecked(sum + BitConverter.ToUInt64(buffer, i));
        }

        return sum;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static (string? ApiKey, string? Username, string? Password) ReadCredentials(OpenSubtitlesTestRequest? overrides)
    {
        var config = Plugin.Instance!.Configuration;

        static string? Pick(string? given, string? saved) => string.IsNullOrWhiteSpace(given) ? saved : given.Trim();

        return (
            Pick(overrides?.ApiKey, config.OpenSubtitlesApiKey),
            Pick(overrides?.Username, config.OpenSubtitlesUsername),
            Pick(overrides?.Password, config.OpenSubtitlesPassword));
    }

    // Every request carries the key and a User-Agent naming the app and its version, which
    // the API insists on, and asks for JSON back.
    private static void Prepare(HttpRequestMessage request, string? apiKey)
    {
        request.Headers.TryAddWithoutValidation("Api-Key", apiKey ?? string.Empty);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd("Jellyfin-Plugin-Lapse v" + Version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("Lapse");
    }
}
