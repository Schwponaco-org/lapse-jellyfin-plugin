// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Net;
using System.Text;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Controllers;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// The lookups and caches that keep big libraries and offline servers fast: the record
/// index, the GitHub failure cache, shared engine probes, MyMemory dedupe.
/// </summary>
public class CachingTests
{
    public CachingTests()
    {
        _ = TestHost.Plugin;
    }

    [Fact]
    public void RecordIndexFindsAddsAndFollowsTheList()
    {
        var config = new PluginConfiguration();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Null(config.FindRecord(first));

        var added = config.GetOrAddRecord(first);
        Assert.Same(added, config.FindRecord(first));
        Assert.Same(added, config.GetOrAddRecord(first));
        Assert.Single(config.MovieRecords);

        // the XML loader fills the list behind the index's back
        var loaded = new MovieSyncRecord { ItemId = second };
        config.MovieRecords.Add(loaded);
        Assert.Same(loaded, config.FindRecord(second));

        // duplicates from an old config: first one wins, like the old FirstOrDefault
        config.MovieRecords.Add(new MovieSyncRecord { ItemId = second });
        Assert.Same(loaded, config.FindRecord(second));
    }

    [Theory]
    [InlineData("/media/Movies", "/media/Movies/Film/Film.mkv", true)]
    [InlineData("/media/Movies/", "/media/Movies", true)]
    [InlineData("/media/Movies", "/media/Movies Extra/Film.mkv", false)]
    [InlineData("/media/Movies", "/media/Moviesx", false)]
    [InlineData("D:\\Media\\TV", "d:\\media\\tv\\Show\\S01E01.mkv", true)]
    public void IgnoreRulesCoverTheFolderAndNothingNextToIt(string rule, string path, bool ignored)
    {
        var config = new PluginConfiguration();
        config.IgnoreRules.Add(new IgnoreRule { Path = rule });

        Assert.Equal(ignored, config.IsIgnored(Array.Empty<Guid>(), path));
    }

    [Fact]
    public void SavingARecordTwiceUpdatesTheSameSubtitleEntry()
    {
        var itemId = Guid.NewGuid();
        var path = Path.Combine(TestHost.NewFolder(), "Film.en.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");

        SyncQueueManager.SaveRecord(itemId, MovieSyncStatus.Synced, null, syncedPaths: new[] { path }, deferSave: true);
        SyncQueueManager.SaveRecord(itemId, MovieSyncStatus.Synced, null, syncedPaths: new[] { path }, deferSave: true);

        lock (Plugin.ConfigurationLock)
        {
            var record = TestHost.Plugin.Configuration.FindRecord(itemId);
            Assert.NotNull(record);
            Assert.Single(record!.SyncedSubtitles);
            Assert.True(SyncQueueManager.IsStillSynced(record, path));
        }
    }

    [Fact]
    public async Task GitHubFailuresAreRememberedSoOfflineServersDontHang()
    {
        var api = new CountingApi(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new GitHubReleaseClient(api, NullLogger<GitHubReleaseClient>.Instance);

        Assert.Null(await client.GetLatestTagAsync("owner/repo"));
        Assert.Null(await client.GetLatestTagAsync("owner/repo"));
        Assert.Equal(1, api.Calls);

        // a forced check always asks
        Assert.Null(await client.GetLatestTagAsync("owner/repo", force: true));
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task AFailedForcedCheckKeepsTheLastGoodTagForEveryoneElse()
    {
        var up = true;
        var api = new CountingApi(_ => up
            ? Json("{\"tag_name\":\"v2.2.4\"}")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new GitHubReleaseClient(api, NullLogger<GitHubReleaseClient>.Instance);

        Assert.Equal("v2.2.4", await client.GetLatestTagAsync("owner/repo"));

        up = false;
        Assert.Null(await client.GetLatestTagAsync("owner/repo", force: true));
        Assert.Equal("v2.2.4", await client.GetLatestTagAsync("owner/repo"));
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task MyMemoryOnlyAsksOncePerDistinctLine()
    {
        var api = new CountingApi(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var text = query["q"]!.ToUpperInvariant();
            return Json("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"" + text + "\",\"match\":0.9}}");
        });

        var provider = new MyMemoryTranslationProvider(api, NullLogger<MyMemoryTranslationProvider>.Instance);
        var result = await provider.TranslateAsync(new[] { "yes", "no", "yes", string.Empty, "no" }, "en", "da", CancellationToken.None);

        Assert.Equal(new[] { "YES", "NO", "YES", string.Empty, "NO" }, result.Select(r => r.Text));
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task ProbesAskedAtTheSameTimeShareOneRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = TestHost.NewFolder();
        var counter = Path.Combine(folder, "runs");
        var engine = Path.Combine(folder, "lapse");

        await File.WriteAllTextAsync(engine, $$"""
            #!/bin/sh
            echo x >> "{{counter}}"
            sleep 0.2
            if [ "$1" = "--capabilities" ]; then echo '{"version":"9.9.9","flags":["--output"]}'; exit 0; fi
            exit 1
            """);
        File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var probe = new EngineCapabilityProbe(NullLogger<EngineCapabilityProbe>.Instance);
        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => probe.ProbeAsync(engine)));

        Assert.All(answers, a => Assert.Equal("9.9.9", a.Version));

        // --formats and --capabilities, once each, not once per caller
        Assert.Equal(2, File.ReadAllLines(counter).Length);
    }

    [Theory]
    [InlineData("OpenDyslexic", "OpenDyslexic")]
    [InlineData("  Comic Sans MS ", "Comic Sans MS")]
    [InlineData("x\"; } body { display:none", "x  body  displaynone")]
    [InlineData("Font\nName", "FontName")]
    [InlineData("\"\";{}", null)]
    [InlineData(null, null)]
    public void FontNamesCantBreakOutOfTheStylesheet(string? typed, string? expected)
    {
        Assert.Equal(expected, LapseController.NormalizeFontFamily(typed));
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class CountingApi : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        private int _calls;

        public CountingApi(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _answer = answer;
        }

        public int Calls => Volatile.Read(ref _calls);

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_answer(request));
        }
    }
}
