// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// What each engine is told on its command line, and what is read back out of what it
/// prints. The three engines take their arguments three different ways, and a wrong flag
/// is an engine that refuses to start.
/// </summary>
public class EngineArgumentTests
{
    public EngineArgumentTests()
    {
        _ = TestHost.Plugin;
    }

    private static EngineRuntimeInfo Runtime(params string[] flags)
    {
        var runtime = new EngineRuntimeInfo { Probed = true, UsageText = "lapse <video> [subtitle] [auto|ols|nosplit|split]" };
        runtime.Flags.AddRange(flags);
        return runtime;
    }

    private static EngineParameterValues Values(IEngine engine, params (string Key, string? Value)[] saved)
    {
        return new EngineParameterValues(engine.Descriptor.Parameters, saved.ToDictionary(s => s.Key, s => s.Value));
    }

    private static EngineRunOptions Options(IEngine engine, EngineRuntimeInfo runtime, EngineParameterValues values, SyncMode mode = SyncMode.Auto)
    {
        return new EngineRunOptions
        {
            ReferencePath = "/m/Film.mkv",
            InputPath = "/m/Film.en.srt",
            OutputPath = "/m/Film.en.srt.lapse-tmp.srt",
            Mode = mode,
            Penalty = 12,
            Runtime = runtime,
            Parameters = values
        };
    }

    [Fact]
    public void LapseKeepsAFractionalFrameRate()
    {
        var engine = new LapseEngine();
        var args = engine.BuildArguments(Options(engine, Runtime("--output", "--fps"), Values(engine, ("fps", "23.976"))));

        // It used to be rounded to 24 on the way through, which drifts a MicroDVD file
        // by seven seconds over a two hour film.
        var at = args.ToList().IndexOf("--fps");
        Assert.Equal("23.976", args[at + 1]);
    }

    [Fact]
    public void LapseOnlyPassesFlagsTheInstalledBuildKnows()
    {
        var engine = new LapseEngine();
        var values = Values(engine, ("fps", "25"), ("fullScan", "true"), ("encoding", "utf8"));

        var old = engine.BuildArguments(Options(engine, Runtime("--output"), values));
        var current = engine.BuildArguments(Options(engine, Runtime("--output", "--fps", "--full-scan", "--encoding", "--json", "--no-backup"), values));

        Assert.DoesNotContain("--fps", old);
        Assert.DoesNotContain("--full-scan", old);
        Assert.DoesNotContain("--encoding", old);
        Assert.Contains("--fps", current);
        Assert.Contains("--full-scan", current);
        Assert.Contains("--no-backup", current);
        Assert.Contains("--json", current);
        Assert.Equal("utf8", current[current.ToList().IndexOf("--encoding") + 1]);
    }

    [Fact]
    public void LapseAsksForForceOnceWhetherItIsSetOrRetried()
    {
        var engine = new LapseEngine();
        var runtime = Runtime("--output", "--force");

        var plain = engine.BuildArguments(Options(engine, runtime, Values(engine)));
        var retried = Options(engine, runtime, Values(engine));
        retried.ForceAnyway = true;
        var setAndRetried = Options(engine, runtime, Values(engine, ("force", "true")));
        setAndRetried.ForceAnyway = true;

        Assert.DoesNotContain("--force", plain);
        Assert.Single(engine.BuildArguments(retried), a => a == "--force");
        Assert.Single(engine.BuildArguments(setAndRetried), a => a == "--force");
    }

    [Fact]
    public void LapseSplitModeTakesItsPenaltyAndAnOldBuildGetsNoAutoMode()
    {
        var engine = new LapseEngine();

        var split = engine.BuildArguments(Options(engine, Runtime("--output"), Values(engine), SyncMode.Split));
        Assert.Equal(new[] { "/m/Film.mkv", "/m/Film.en.srt", "split", "12", "--output" }, split.Take(5));

        var oldRuntime = new EngineRuntimeInfo { Probed = true, UsageText = "lapse <video> <subtitle> [nosplit|split]" };
        var auto = engine.BuildArguments(Options(engine, oldRuntime, Values(engine)));

        // No --output on that build either, so it is pointed at the work file.
        Assert.Equal(new[] { "/m/Film.mkv", "/m/Film.en.srt.lapse-tmp.srt", "nosplit" }, auto);
    }

    [Fact]
    public void AlassLeavesItsOwnDefaultsOffTheCommandLine()
    {
        var engine = new AlassEngine();

        var defaults = engine.BuildArguments(Options(engine, Runtime(), Values(engine), SyncMode.Standard));
        Assert.Equal(new[] { "/m/Film.mkv", "/m/Film.en.srt", "/m/Film.en.srt.lapse-tmp.srt", "--no-split" }, defaults);

        var changed = engine.BuildArguments(Options(
            engine,
            Runtime(),
            Values(engine, ("interval", "5"), ("subFpsInc", "23.976"), ("encodingInc", "windows-1252"), ("disableFpsGuessing", "true")),
            SyncMode.Split));

        Assert.Equal(
            new[]
            {
                "/m/Film.mkv", "/m/Film.en.srt", "/m/Film.en.srt.lapse-tmp.srt",
                "--split-penalty", "12", "--interval", "5", "--sub-fps-inc", "23.976",
                "--encoding-inc", "windows-1252", "--disable-fps-guessing"
            },
            changed);
    }

    [Fact]
    public void FfsubsyncIsToldWhereFfmpegIsAndOnlyWhatChanged()
    {
        var engine = new FfsubsyncEngine();
        var options = Options(engine, Runtime(), Values(engine, ("vad", "auditok"), ("gss", "true"), ("maxOffsetSeconds", "600")), SyncMode.Standard);
        options.FfmpegDirectory = "/usr/lib/jellyfin-ffmpeg";

        var args = engine.BuildArguments(options);

        Assert.Equal(
            new[]
            {
                "/m/Film.mkv", "-i", "/m/Film.en.srt", "-o", "/m/Film.en.srt.lapse-tmp.srt",
                "--ffmpeg-path", "/usr/lib/jellyfin-ffmpeg", "--vad", "auditok", "--gss"
            },
            args);
    }

    [Theory]
    [InlineData("shift: 1.2496s", 1250)]
    [InlineData("shift: -0.0004s", 0)]
    [InlineData("offset 12.0s", 12000)]
    public void AlassOffsetsAreRoundedNotCutOff(string stdout, int expected)
    {
        var result = new AlassEngine().ParseResult(stdout, string.Empty, 0, SyncMode.Standard, 7);
        Assert.Equal(expected, result.OffsetMs);
    }

    [Fact]
    public void FfsubsyncOffsetsAreRoundedAndFailuresSummarised()
    {
        var engine = new FfsubsyncEngine();

        Assert.Equal(-2001, engine.ParseResult(string.Empty, "INFO: offset seconds: -2.0008\n", 0, SyncMode.Standard, 7).OffsetMs);

        var failed = engine.ParseResult(string.Empty, "Traceback (most recent call last):\n  File \"x.py\", line 1\nValueError: no speech found\n", 1, SyncMode.Standard, 7);
        Assert.False(failed.Success);
        Assert.Equal("Traceback (most recent call last): ValueError: no speech found", failed.Error);
    }

    [Fact]
    public void LapseReadsAJsonOffsetToTheNearestMillisecond()
    {
        var result = new LapseEngine().ParseResult("{\"mode\":\"auto/shifted\",\"offset_ms\":1249.6,\"verdict\":\"solid\",\"written\":true}", string.Empty, 0, SyncMode.Auto, 6);

        Assert.Equal(1250, result.OffsetMs);
        Assert.Equal(SyncMode.Auto, result.Mode);
        Assert.Null(result.Slope);
    }

    [Fact]
    public void LapseSaysWhyItWroteNothing()
    {
        var result = new LapseEngine().ParseResult("{\"verdict\":\"nothing\",\"written\":false,\"why\":\"the audio backs no answer up\"}", string.Empty, 0, SyncMode.Auto, 6);

        Assert.False(result.Success);
        Assert.Equal("nothing", result.Verdict);
        Assert.Equal("the audio backs no answer up", result.Error);
    }

    [Fact]
    public void DecoderNoiseIsCountedNotRepeated()
    {
        var noise = string.Concat(Enumerable.Repeat("[mp3 @ 0x7ff935c2ea80] Header missing\n", 300));

        Assert.StartsWith("The engine could not decode this file's audio - it produced 300 decoder errors", EngineResults.Summarize(noise), StringComparison.Ordinal);
        Assert.Equal(
            "Could not open file (plus 300 decoder warnings, see the server log)",
            EngineResults.Summarize(noise + "Could not open file\nCould not open file\n"));
        Assert.Null(EngineResults.Summarize("  \n"));
    }

    [Theory]
    [InlineData("/m/Film.en.srt", OutputMode.SidecarOnly, null, "/m/Film.en.shifted.srt")]
    [InlineData("/m/Film.en.shifted.srt", OutputMode.SidecarWithBackup, null, "/m/Film.en.shifted.srt")]
    [InlineData("/m/Film.en.srt", OutputMode.OverwriteWithBackup, null, "/m/Film.en.srt")]
    [InlineData("/m/Film.en.sub", OutputMode.OverwriteNoBackup, "srt", "/m/Film.en.srt")]
    [InlineData("/m/Film.en.sub", OutputMode.SidecarOnly, "srt", "/m/Film.en.shifted.srt")]
    [InlineData("/m/Film.en.SRT", OutputMode.SidecarOnly, "srt", "/m/Film.en.shifted.SRT")]
    public void ResultsLandWhereTheOutputModeSays(string input, OutputMode mode, string? format, string expected)
    {
        Assert.Equal(expected, EngineRunner.ResolveDestination(input, mode, format));
    }

    [Fact]
    public void ABackupIsOnlyTakenWhenAskedForAndWhenThereIsSomethingToKeep()
    {
        var folder = TestHost.NewFolder();
        var target = Path.Combine(folder, "Film.en.srt");

        Assert.Null(EngineRunner.TakeBackup(target, OutputMode.OverwriteWithBackup));

        File.WriteAllText(target, "old");
        Assert.Null(EngineRunner.TakeBackup(target, OutputMode.OverwriteNoBackup));
        Assert.Null(EngineRunner.TakeBackup(target, OutputMode.SidecarOnly));

        var backup = EngineRunner.TakeBackup(target, OutputMode.OverwriteWithBackup);
        Assert.Equal(target + ".bak", backup);
        Assert.Equal("old", File.ReadAllText(backup!));
    }

    [Fact]
    public void ParametersFallBackToTheEngineDefaultUnlessBlankWasChosen()
    {
        var engine = new LapseEngine();
        var values = Values(engine, ("audioTrack", string.Empty), ("snap", "120"));

        Assert.Equal(string.Empty, values.GetString("audioTrack"));
        Assert.False(values.ShouldPass("audioTrack"));
        Assert.True(values.ShouldPass("snap"));
        Assert.False(values.GetBool("noCache"));

        var alass = new AlassEngine();
        var alassValues = Values(alass, ("interval", "1"));
        Assert.False(alassValues.ShouldPass("interval"));
        Assert.Equal(30, alassValues.GetNumber("subFpsRef"));
    }

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(23.976, "23.976")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(8.00000001, "8")]
    public void NumbersAreWrittenTheWayACommandLineWantsThem(double value, string expected)
    {
        Assert.Equal(expected, EngineParameterValues.Format(value));
    }
}
