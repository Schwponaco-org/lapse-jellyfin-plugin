// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Net;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// What happens when a bulk run is over: the ntfy and Discord notification.
/// </summary>
public class BulkRunTests
{
    public BulkRunTests()
    {
        _ = TestHost.Plugin;
    }

    [Theory]
    [InlineData("https://ntfy.sh/lapse-runs", false, true)]
    [InlineData("http://192.168.1.10:8080/lapse", false, true)]
    [InlineData("ftp://ntfy.sh/lapse", false, false)]
    [InlineData("ntfy.sh/lapse", false, false)]
    [InlineData("https://discord.com/api/webhooks/1/abc", true, true)]
    [InlineData("https://discordapp.com/api/webhooks/1/abc", true, true)]
    [InlineData("http://discord.com/api/webhooks/1/abc", true, false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/abc", true, false)]
    [InlineData("https://discord.com@192.168.1.1/api/webhooks/1/abc", true, false)]
    [InlineData("https://discord.com/api/v10/users/@me", true, false)]
    [InlineData("http://192.168.1.10:8080/lapse", true, false)]
    public void OnlyTakesNotificationUrlsItShould(string url, bool discord, bool allowed)
    {
        Assert.Equal(allowed, RunNotifier.IsAllowedUrl(url, discord));
    }

    [Fact]
    public void DescribesARunInOneLine()
    {
        var started = new DateTime(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
        var run = new BulkRun
        {
            Name = "Scheduled sync",
            StartedUtc = started,
            FinishedUtc = started.AddMinutes(72).AddSeconds(5),
            Synced = 40,
            Unsure = 2,
            Failed = 1,
            Skipped = 10
        };

        Assert.Equal(
            "LAPSE: Scheduled sync is done. 40 synced, 2 unsure, 1 failed, 10 skipped, took 1h 12m.",
            RunNotifier.Describe(run));

        run.FinishedUtc = started.AddSeconds(250);
        run.Stopped = true;
        Assert.EndsWith("took 4m 10s. It was stopped by hand.", RunNotifier.Describe(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostsToNtfyAndDiscordAndSwallowsFailures()
    {
        var config = TestHost.Plugin.Configuration;
        config.NtfyUrl = "https://ntfy.example/lapse";
        config.DiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc";

        try
        {
            var sent = new List<(string Url, string Body)>();
            var api = new FakeApi(request =>
            {
                sent.Add((request.RequestUri!.AbsoluteUri, request.Content!.ReadAsStringAsync().Result));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

            var run = new BulkRun { Name = "Bulk sync", Synced = 3 };
            await new RunNotifier(api, NullLogger<RunNotifier>.Instance).NotifyAsync(run);

            Assert.Equal(2, sent.Count);
            Assert.Equal("https://ntfy.example/lapse", sent[0].Url);
            Assert.Equal(RunNotifier.Describe(run), sent[0].Body);
            Assert.Equal("https://discord.com/api/webhooks/1/abc", sent[1].Url);
            Assert.Equal("{\"content\":\"" + RunNotifier.Describe(run) + "\"}", sent[1].Body);

            // a server that's down or says no is only logged
            var down = new FakeApi(_ => throw new HttpRequestException("Connection refused"));
            await new RunNotifier(down, NullLogger<RunNotifier>.Instance).NotifyAsync(run);

            var refused = new FakeApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            await new RunNotifier(refused, NullLogger<RunNotifier>.Instance).NotifyAsync(run);
        }
        finally
        {
            config.NtfyUrl = null;
            config.DiscordWebhookUrl = null;
        }
    }

    private sealed class FakeApi : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public FakeApi(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _answer = answer;
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_answer(request));
        }
    }
}
