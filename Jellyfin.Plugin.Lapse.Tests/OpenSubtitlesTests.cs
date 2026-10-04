// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// The OpenSubtitles flow against a stand-in for their API, which checks each request is
/// shaped the way the real one insists on.
/// </summary>
public class OpenSubtitlesTests
{
    public OpenSubtitlesTests()
    {
        _ = TestHost.Plugin;
    }

    [Fact]
    public void BuildsTheQueryTheWayTheApiWantsIt()
    {
        var query = OpenSubtitlesService.BuildQuery(new Dictionary<string, string>
        {
            ["query"] = "Ocean's Eleven (2001)",
            ["languages"] = "da,en",
            ["imdb_id"] = "240772",
            ["episode_number"] = string.Empty
        });

        Assert.Equal("imdb_id=240772&languages=da%2Cen&query=ocean%27s+eleven+%282001%29", query);
    }

    [Fact]
    public void HashesTheWayOpenSubtitlesDoes()
    {
        var path = Path.Combine(TestHost.NewFolder(), "film.mkv");
        var bytes = new byte[200_000];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31 % 251);
        }

        File.WriteAllBytes(path, bytes);

        // Worked out independently: size plus the 64-bit little endian sums of the first
        // and last 64KB, wrapping, in hex.
        ulong expected = (ulong)bytes.Length;
        for (var i = 0; i < 65536; i += 8)
        {
            expected = unchecked(expected + BitConverter.ToUInt64(bytes, i) + BitConverter.ToUInt64(bytes, bytes.Length - 65536 + i));
        }

        Assert.Equal(expected.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), OpenSubtitlesService.ComputeMovieHash(path));
        Assert.Null(OpenSubtitlesService.ComputeMovieHash(Path.Combine(TestHost.NewFolder(), "missing.mkv")));
    }

    [Fact]
    public async Task SearchesSignsInDownloadsAndSaves()
    {
        var config = TestHost.Plugin.Configuration;
        config.OpenSubtitlesEnabled = true;
        config.OpenSubtitlesApiKey = "key-123";
        config.OpenSubtitlesUsername = "someone";
        config.OpenSubtitlesPassword = "secret";
        config.OpenSubtitlesLanguage = "Danish, en";

        var folder = TestHost.NewFolder();
        var video = Path.Combine(folder, "Fargo (1996).mkv");
        await File.WriteAllBytesAsync(video, new byte[150_000]);
        var hash = OpenSubtitlesService.ComputeMovieHash(video)!;

        var movie = new Movie { Name = "Fargo", ProductionYear = 1996, Path = video };
        movie.SetProviderId(MetadataProvider.Imdb, "tt0116282");

        var api = new FakeApi();
        api.On(HttpMethod.Get, "https://api.opensubtitles.com/api/v1/subtitles?imdb_id=116282&languages=da%2Cen&moviehash=" + hash, request =>
        {
            Assert.Equal("key-123", request.Headers.GetValues("Api-Key").Single());
            Assert.StartsWith("Jellyfin-Plugin-Lapse v", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Json("""
                {"data":[
                  {"attributes":{"language":"en","download_count":900,"release":"Fargo.1996.1080p","moviehash_match":true,"files":[{"file_id":11,"file_name":"fargo.en.srt"}]}},
                  {"attributes":{"language":"da","download_count":40,"release":"Fargo.1996.DVDRip","hearing_impaired":true,"files":[{"file_id":22,"file_name":"fargo.da.srt"}]}},
                  {"attributes":{"language":"da","download_count":5000,"ai_translated":true,"files":[{"file_id":33,"file_name":"fargo.da.ai.srt"}]}},
                  {"attributes":{"language":"da","download_count":9000,"files":[{"file_id":44,"file_name":"cd1.srt"},{"file_id":45,"file_name":"cd2.srt"}]}}
                ]}
                """);
        });
        api.On(HttpMethod.Post, "https://api.opensubtitles.com/api/v1/login", _ =>
            Json("""{"token":"tok-1","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":1000}}"""));
        api.On(HttpMethod.Post, "https://vip-api.opensubtitles.com/api/v1/download", request =>
        {
            Assert.Equal("Bearer tok-1", request.Headers.Authorization!.ToString());
            Assert.Contains("\"file_id\":22", request.Content!.ReadAsStringAsync().Result, StringComparison.Ordinal);
            return Json("""{"link":"https://www.opensubtitles.com/download/abc/fargo.da.srt","file_name":"fargo.da.srt","remaining":17}""");
        });
        api.On(HttpMethod.Get, "https://www.opensubtitles.com/download/abc/fargo.da.srt", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHej\n\n2\n00:00:03,000 --> 00:00:04,000\nMed dig\n") });

        using var service = new OpenSubtitlesService(api, new SubtitleLanguages(Languages.Create()), NullLogger<OpenSubtitlesService>.Instance);

        var search = await service.SearchAsync(movie, null);

        // Danish first because it was asked for first, a person's subtitle before an AI
        // one, and the two-CD one left out entirely.
        Assert.Null(search.Error);
        Assert.Equal(new long[] { 22, 33, 11 }, search.Candidates.Select(c => c.FileId).ToArray());

        var fetched = await service.TryFetchAsync(movie);

        Assert.True(fetched.Success, fetched.Error);
        Assert.Equal(Path.Combine(folder, "Fargo (1996).da.sdh.srt"), fetched.Path);
        Assert.Equal(17, fetched.RemainingDownloads);
        Assert.Contains("Med dig", await File.ReadAllTextAsync(fetched.Path!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaysWhyWhenTheKeyIsRefused()
    {
        var config = TestHost.Plugin.Configuration;
        config.OpenSubtitlesApiKey = "bad";

        var api = new FakeApi { Fallback = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"You cannot consume this service"}""") } };
        using var service = new OpenSubtitlesService(api, new SubtitleLanguages(Languages.Create()), NullLogger<OpenSubtitlesService>.Instance);

        var status = await service.TestAsync(new Data.OpenSubtitlesTestRequest { Username = string.Empty });

        Assert.False(status.Success);
        Assert.Contains("API key", status.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeApi : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();

        public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

        public void On(HttpMethod method, string url, Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _routes[method + " " + url] = answer;
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Method + " " + request.RequestUri!.AbsoluteUri;

            if (_routes.TryGetValue(key, out var answer))
            {
                return Task.FromResult(answer(request));
            }

            if (Fallback is not null)
            {
                return Task.FromResult(Fallback(request));
            }

            throw new InvalidOperationException("Unexpected request: " + key);
        }
    }

    /// <summary>
    /// Jellyfin's language table, cut down to the languages these tests use.
    /// </summary>
    public class Languages : DispatchProxy
    {
        private static readonly CultureDto[] Known =
        {
            new("English", "English", "en", new[] { "eng" }),
            new("Danish", "Danish", "da", new[] { "dan" })
        };

        public static ILocalizationManager Create() => Create<ILocalizationManager, Languages>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILocalizationManager.FindLanguageInfo) && args?[0] is string wanted)
            {
                return Known.FirstOrDefault(c =>
                    string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.TwoLetterISOLanguageName, wanted, StringComparison.OrdinalIgnoreCase)
                    || c.ThreeLetterISOLanguageNames.Contains(wanted, StringComparer.OrdinalIgnoreCase));
            }

            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
