// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

public class EngineTests
{
    public EngineTests()
    {
        _ = TestHost.Plugin;
    }

    private static EngineRuntimeInfo Runtime(params string[] flags)
    {
        var runtime = new EngineRuntimeInfo { Probed = true, UsageText = "lapse <video> [subtitle] [auto|ols|nosplit|split]" };
        runtime.Flags.AddRange(flags);
        return runtime;
    }

    [Fact]
    public void AnExtractedTrackIsSyncedAgainstTheAudio()
    {
        var engine = new LapseEngine();
        var options = new EngineRunOptions
        {
            ReferencePath = "/m/Film.mkv",
            InputPath = "/m/Film.eng.stream2.srt",
            OutputPath = "/m/Film.eng.stream2.srt.lapse-tmp.srt",
            Mode = SyncMode.Auto,
            Runtime = Runtime("--output", "--json", "--no-embedded"),
            Parameters = new EngineParameterValues(engine.Descriptor.Parameters, null),
            IgnoreEmbedded = true
        };

        var args = engine.BuildArguments(options);

        Assert.Single(args, a => a == "--no-embedded");
    }

    [Theory]
    [InlineData("/m/Film.mkv", "/m/Film.eng.stream2.srt", true)]
    [InlineData("/m/Film.mkv", "/m/Film.eng.forced.stream12.shifted.srt", true)]
    [InlineData("/m/Film.mkv", "/m/Film.eng.track3.srt", true)]
    [InlineData("/m/Film.mkv", "/m/Film.en.srt", false)]
    [InlineData("/m/Film.mkv", "/other/Film.eng.stream2.srt", false)]
    [InlineData("/m/Film.en.srt", "/m/Film.en.stream2.srt", false)]
    [InlineData("/m/Film.mkv", "/m/Film.streamer.srt", false)]
    public void RecognisesAVideosOwnTracks(string video, string subtitle, bool expected)
    {
        Assert.Equal(expected, EngineRunner.IsOwnTrack(video, subtitle));
    }

    [Fact]
    public void RecognisesItsOwnSidecars()
    {
        var all = new[] { "/m/Film.en.srt", "/m/Film.en.shifted.srt", "/m/Film.da.shifted.srt", "/m/Old.sub", "/m/Old.shifted.srt" };

        Assert.True(EngineRunner.IsSidecarOfAnother("/m/Film.en.shifted.srt", all));
        Assert.False(EngineRunner.IsSidecarOfAnother("/m/Film.da.shifted.srt", all));
        Assert.True(EngineRunner.IsSidecarOfAnother("/m/Old.shifted.srt", all));
        Assert.False(EngineRunner.IsSidecarOfAnother("/m/Film.en.srt", all));
    }

    [Fact]
    public void ReadsTheJsonReport()
    {
        var engine = new LapseEngine();
        var stdout = "{\"mode\":\"auto/drifting\",\"reference\":\"vad\",\"offset_ms\":-459,\"ratio\":0.95904,\"confidence\":0.9,\"margin\":3,\"sigma\":11.9,\"agreement\":1,\"verdict\":\"solid\",\"coverage\":1,\"cues\":50,\"ignored_cues\":0,\"parts\":1,\"snapped\":0,\"written\":true,\"output\":\"/x.srt\",\"splits\":[]}";

        var result = engine.ParseResult(stdout, string.Empty, 0, SyncMode.Auto, 6);

        Assert.True(result.Success);
        Assert.Equal("solid", result.Verdict);
        Assert.Equal(-459, result.OffsetMs);
        Assert.Equal(SyncMode.Ols, result.Mode);
        Assert.NotNull(result.Slope);
    }

    [Fact]
    public void ABatchFailureFallsBackToTheErrorText()
    {
        var result = new LapseEngine().ParseResult(string.Empty, "Only 4 cues in x.srt, that is not enough to go on. Pass --force to sync it anyway.\n", 1, SyncMode.Auto, 6);

        Assert.False(result.Success);
        Assert.Contains("Pass --force", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, false, false, false, false)]
    public void CountsAsSyncedOnlyWhenTheEngineStoodBehindIt(bool success, bool skipped, bool alreadyInSync, bool unconfirmed, bool expected)
    {
        var result = new SyncResult { Success = success, Skipped = skipped, AlreadyInSync = alreadyInSync, Unconfirmed = unconfirmed };
        Assert.Equal(expected, result.CountsAsSynced);
    }

    [Fact]
    public void FindsAChecksumAndPinsTheRelease()
    {
        var sums = "ac1a3194b2454982252f1f8d64a6aefd9b542df6459c32312dee18c98ad9fd1e  lapse-linux-amd64\n"
            + "9a7471f75460948be73ff017ab9b9fcaf9fb00dd8c59e50a182a965c466c6e88 *lapse-linux-amd64.tar.gz\n";

        Assert.Equal("9a7471f75460948be73ff017ab9b9fcaf9fb00dd8c59e50a182a965c466c6e88", EngineInstaller.FindChecksum(sums, "lapse-linux-amd64.tar.gz"));
        Assert.Null(EngineInstaller.FindChecksum(sums, "lapse-windows-x64.zip"));

        Assert.Equal(
            "https://github.com/o/r/releases/download/v2.2.4/a.tar.gz",
            EngineInstaller.PinToRelease("https://github.com/o/r/releases/latest/download/a.tar.gz", "v2.2.4"));
        Assert.Equal(
            "https://github.com/o/r/releases/latest/download/a.tar.gz",
            EngineInstaller.PinToRelease("https://github.com/o/r/releases/latest/download/a.tar.gz", null));
    }

    [Fact]
    public void BatchLinesCarryWhatTheyCanAndRefuseWhatTheyCant()
    {
        Assert.Equal("\"/m/A Film.mkv\" \"auto\"", EngineBatchSession.BuildJobLine(new[] { "/m/A Film.mkv", "auto" }));
        Assert.True(EngineBatchSession.CanCarry(new[] { "/m/A\tFilm.mkv", "--json" }));
        Assert.False(EngineBatchSession.CanCarry(new[] { "/m/The \"Best\" Film.mkv" }));
        Assert.False(EngineBatchSession.CanCarry(new[] { "/m/a\nb.mkv" }));
        Assert.False(EngineBatchSession.CanCarry(new[] { string.Empty }));
    }

    [Fact]
    public void ALaterRunOnTheSameFileSupersedesTheEarlierOne()
    {
        var config = new PluginConfiguration();
        var first = new SyncHistoryEntry { OutputPath = "/m/a.srt", BackupPath = "/m/a.srt.bak" };
        var other = new SyncHistoryEntry { OutputPath = "/m/b.srt", BackupPath = "/m/b.srt.bak" };
        var second = new SyncHistoryEntry { OutputPath = "/m/a.srt", BackupPath = "/m/a.srt.bak" };

        SyncHistoryService.Append(config, first);
        SyncHistoryService.Append(config, other);
        SyncHistoryService.Append(config, second);

        Assert.True(first.Superseded);
        Assert.False(other.Superseded);
        Assert.False(second.Superseded);
        Assert.False(SyncHistoryService.CanRevert(first));
    }

    [Theory]
    [InlineData("1.2.3", "v1.2.4", true)]
    [InlineData("v2.2.4", "v2.2.4", false)]
    [InlineData("2.2.4", "v2.2.3", false)]
    [InlineData(null, "v2.2.4", false)]
    public void ComparesReleaseTags(string? installed, string latest, bool newer)
    {
        Assert.Equal(newer, GitHubReleaseClient.IsNewer(installed, latest));
    }

    [Fact]
    public void ASubtitleRestoredOrReplacedSinceItsSyncIsNoLongerSynced()
    {
        var path = Path.Combine(TestHost.NewFolder(), "Film.en.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var written = File.GetLastWriteTimeUtc(path);

        var record = new MovieSyncRecord();
        record.SyncedSubtitles.Add(new SubtitleSyncRecord { Path = path, LastSyncUtc = DateTime.UtcNow, FileWriteUtc = written });

        Assert.True(SyncQueueManager.IsStillSynced(record, path));

        // An older copy put back, with its old time kept.
        File.SetLastWriteTimeUtc(path, written.AddDays(-3));
        Assert.False(SyncQueueManager.IsStillSynced(record, path));

        // A new one written over it.
        File.SetLastWriteTimeUtc(path, written.AddMinutes(1));
        Assert.False(SyncQueueManager.IsStillSynced(record, path));

        Assert.False(SyncQueueManager.IsStillSynced(record, path + ".other"));
    }
}
