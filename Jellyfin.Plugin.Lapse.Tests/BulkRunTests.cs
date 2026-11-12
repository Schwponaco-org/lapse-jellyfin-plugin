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
/// What happens when a bulk run is over: the ntfy and Discord notification, and undoing
/// the whole run at once.
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
    public void DescribesASyncInPlainWords()
    {
        Assert.Equal("Moved 4.2 s earlier.", SyncQueueManager.DescribeForHistory(new SyncResult { OffsetMs = -4230 }));
        Assert.Equal(
            "Moved 534 s later. Written beside the original for you to check.",
            SyncQueueManager.DescribeForHistory(new SyncResult { OffsetMs = 534080, Unconfirmed = true }));
        Assert.Equal(
            "No match in the audio. Written beside the original for you to check.",
            SyncQueueManager.DescribeForHistory(new SyncResult { OffsetMs = -8707, Verdict = "nothing", Unconfirmed = true }));
        Assert.Equal(
            "Moved in 2 parts, from 1.5 s earlier to 300 ms later.",
            SyncQueueManager.DescribeForHistory(new SyncResult { PartOffsetsMs = new[] { -1500, 300 } }));
    }

    [Fact]
    public void KeepsAShiftByHandOnlyWhenItCanBeUndone()
    {
        var folder = Directory.CreateTempSubdirectory("lapse-shift-").FullName;
        var original = Path.Combine(folder, "Film.en.srt");
        var sidecar = Path.Combine(folder, "Film.en.shifted.srt");
        File.WriteAllText(original, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        File.WriteAllText(sidecar, "1\n00:00:01,250 --> 00:00:02,250\nHi\n");
        var history = TestHost.Plugin.Configuration.History;
        var itemId = Guid.NewGuid();

        SyncQueueManager.RecordShift(itemId, original, new ShiftResult { OutputPath = original }, 250);
        Assert.DoesNotContain(history, h => h.ItemId == itemId);

        SyncQueueManager.RecordShift(itemId, original, new ShiftResult { OutputPath = sidecar }, 250);
        var entry = Assert.Single(history, h => h.ItemId == itemId);
        Assert.True(entry.WroteNewFile);
        Assert.Null(entry.InputPath);
        Assert.Equal("Shifted 250 ms later by hand.", entry.Detail);
        Assert.True(SyncHistoryService.CanRevert(entry));
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

    [Fact]
    public void KeepsOnlyTheLastFewRuns()
    {
        var service = new SyncHistoryService(TestHost.ApplicationPaths, NullLogger<SyncHistoryService>.Instance);
        var saved = new List<Guid>();

        for (var i = 0; i < SyncHistoryService.MaxRuns + 2; i++)
        {
            var run = new BulkRun { Name = "Run " + i };
            run.Files.Add(new SyncHistoryEntry { OutputPath = "/m/" + i + ".srt", WroteNewFile = true });
            service.SaveRun(run);
            saved.Add(run.Id);
        }

        var kept = service.GetRuns();
        Assert.Equal(saved.TakeLast(SyncHistoryService.MaxRuns), kept.Select(r => r.Id));
        Assert.Equal("/m/" + (SyncHistoryService.MaxRuns + 1) + ".srt", kept[^1].Files[0].OutputPath);
    }

    [Fact]
    public void UndoingARunPutsBackWhatItCanAndLeavesNewerWorkAlone()
    {
        var folder = TestHost.NewFolder();
        var config = TestHost.Plugin.Configuration;
        var service = new SyncHistoryService(TestHost.ApplicationPaths, NullLogger<SyncHistoryService>.Instance);

        // overwritten with a backup
        var replaced = Written(folder, "Film.en.srt", "synced");
        File.WriteAllText(replaced.OutputPath + ".bak", "original");
        replaced.BackupPath = replaced.OutputPath + ".bak";

        // a sidecar the run added
        var added = Written(folder, "Film.en.shifted.srt", "synced");
        added.WroteNewFile = true;

        // that sidecar is an unsure answer still up for review
        var waiting = new SyncCandidateSet { OriginalPath = replaced.OutputPath! };
        waiting.Candidates.Add(new SyncCandidate { Path = added.OutputPath! });

        // Bazarr put a new one there after the run
        var newer = Written(folder, "Film.da.srt", "synced");
        File.WriteAllText(newer.OutputPath + ".bak", "original");
        newer.BackupPath = newer.OutputPath + ".bak";
        File.WriteAllText(newer.OutputPath!, "from bazarr");
        File.SetLastWriteTimeUtc(newer.OutputPath!, newer.OutputWriteUtc!.Value.AddMinutes(5));

        // its backup was cleaned up by hand
        var noBackup = Written(folder, "Film.de.srt", "synced");
        noBackup.BackupPath = noBackup.OutputPath + ".bak";

        var run = new BulkRun { Name = "Bulk sync" };
        run.Files.AddRange(new[] { replaced, added, newer, noBackup });

        lock (Plugin.ConfigurationLock)
        {
            run.Files.ForEach(f => SyncHistoryService.Append(config, f));
            config.PendingCandidates.Add(waiting);
        }

        try
        {
            service.SaveRun(run);
            var outcomes = service.UndoRun(run.Id)!;

            Assert.Equal(new[] { true, true, false, false }, outcomes.Select(o => o.File.Reverted));
            Assert.Equal("original", File.ReadAllText(replaced.OutputPath!));
            Assert.False(File.Exists(replaced.BackupPath));
            Assert.False(File.Exists(added.OutputPath));
            Assert.DoesNotContain(waiting, config.PendingCandidates);
            Assert.Equal("from bazarr", File.ReadAllText(newer.OutputPath!));
            Assert.True(File.Exists(newer.BackupPath));
            Assert.Contains("left alone", outcomes[2].Outcome, StringComparison.Ordinal);
            Assert.Contains("nothing left", outcomes[3].Outcome, StringComparison.Ordinal);

            // the history rows say undone too, and the kept run remembers it
            Assert.True(config.History.Find(h => h.Id == replaced.Id)!.Reverted);
            Assert.Equal(2, service.GetRuns().Find(r => r.Id == run.Id)!.Files.Count(f => f.Reverted));

            Assert.Null(service.UndoRun(Guid.NewGuid()));
        }
        finally
        {
            lock (Plugin.ConfigurationLock)
            {
                config.History.RemoveAll(h => run.Files.Exists(f => f.Id == h.Id));
            }
        }
    }

    private static SyncHistoryEntry Written(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return new SyncHistoryEntry { OutputPath = path, InputPath = path, OutputWriteUtc = File.GetLastWriteTimeUtc(path) };
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
