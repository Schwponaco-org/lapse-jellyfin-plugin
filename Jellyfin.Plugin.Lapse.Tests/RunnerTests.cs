// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Reflection;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// A whole sync through the runner, with a shell script standing in for LAPSE. It answers
/// the probe the way the real binary does, copies the subtitle to --output and prints
/// whatever JSON the test left in "lapse.reply" next to it.
/// </summary>
public class RunnerTests
{
    private const string FakeLapse = """
        #!/bin/sh
        echo "$@" >> "$0.calls"
        case "$1" in
          --version) echo "lapse 2.3.0" ;;
          --formats) printf '.srt\n.ass\n' ;;
          --vad) echo silero ;;
          ""|--capabilities)
            if [ -f "$0.usage" ]; then cat "$0.usage" >&2; exit 255; fi
            echo "Usage: lapse <video_or_subtitle> [subtitle] [auto|ols|nosplit|split] [penalty] [--output <path>] [--no-backup] [--no-sidecar] [--no-embedded] [--full-scan] [--no-cache] [--force] [--json] [--quiet] [--dry-run] [--strict] [--confidence N] [--audio-track N] [--sub-track N] [--fps N] [--snap [ms]] [--encoding NAME] [--from video] [--min-shift ms] [--whisper model]" >&2
            exit 255 ;;
          *)
            echo "env LAPSE_WHISPER_CLI=$LAPSE_WHISPER_CLI" >> "$0.calls"
            out=""
            prev=""
            for a in "$@"; do
              [ "$prev" = "--output" ] && out="$a"
              prev="$a"
            done
            cp "$2" "$out"
            cat "$0.reply" ;;
        esac
        """;

    [Fact]
    public async Task ASnappedResultIsWrittenEvenWhenTheOffsetIsTiny()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":8,\"ratio\":1,\"verdict\":\"solid\",\"cues\":86,\"parts\":1,\"snapped\":3,\"written\":true,\"output\":\"x\",\"splits\":[],\"offsets\":[8]}");
        var (video, subtitle) = Film(folder);

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly));

        Assert.True(result.Success);
        Assert.Equal(3, result.Snapped);
        Assert.False(result.AlreadyInSync);
        Assert.Equal(Path.Combine(folder, "Film.en.shifted.srt"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task ATinyOffsetWithNothingSnappedIsLeftAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":8,\"ratio\":1,\"verdict\":\"solid\",\"cues\":86,\"parts\":1,\"snapped\":0,\"written\":true,\"output\":\"x\",\"splits\":[],\"offsets\":[8]}");
        var (video, subtitle) = Film(folder);

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly));

        Assert.True(result.AlreadyInSync);
        Assert.Null(result.Snapped);
        Assert.False(File.Exists(Path.Combine(folder, "Film.en.shifted.srt")));
    }

    [Fact]
    public async Task AMoveBetweenCopiesWritesANewFileNamedForTheTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/drifting\",\"reference\":\"vad\",\"offset_ms\":4,\"ratio\":0.9615,\"verdict\":\"solid\",\"parts\":1,\"snapped\":0,\"written\":true,\"output\":\"x\",\"splits\":[],\"offsets\":[4]}");
        var target = Path.Combine(folder, "Film - Bluray.mkv");
        var source = Path.Combine(folder, "Film - TV.mkv");
        var subtitle = Path.Combine(folder, "Film - TV.en.srt");
        File.WriteAllText(target, "film");
        File.WriteAllText(source, "film");
        File.WriteAllText(subtitle, "1\n00:00:07,000 --> 00:00:08,000\nHello\n");
        var destination = CopiedSubtitleNames.ResolveDestination(target, source, subtitle, "eng", null);

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), target, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly, destination, fromVideoPath: source));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(folder, "Film - Bluray.en.srt"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Null(result.BackupPath);
        Assert.Contains("1\n00:00:07,000", File.ReadAllText(subtitle), StringComparison.Ordinal);
        Assert.Contains("--from " + source, Calls(engine), StringComparison.Ordinal);
        Assert.Contains("--no-embedded", Calls(engine), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderLapseIsNotAskedToMoveASubtitle()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner(
            "{\"verdict\":\"solid\",\"offset_ms\":4,\"written\":true}",
            "Usage: lapse <video_or_subtitle> [subtitle] [auto|ols|nosplit|split] [penalty] [--output <path>] [--no-backup] [--json] [--snap [ms]]");
        var (video, subtitle) = Film(folder);
        var destination = Path.Combine(folder, "Other.en.srt");

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly, destination, fromVideoPath: Path.Combine(folder, "Other.mkv")));

        Assert.False(result.Success);
        Assert.Contains("2.3.0", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("[20,-30]", true)]
    [InlineData("[20,-300]", false)]
    public async Task ASplitIsInSyncOnlyWhenEveryPartIs(string offsets, bool inSync)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/shifted+split\",\"reference\":\"vad\",\"offset_ms\":20,\"ratio\":1,\"verdict\":\"solid\",\"parts\":2,\"snapped\":0,\"written\":true,\"output\":\"x\",\"splits\":[1],\"offsets\":" + offsets + "}");
        var (video, subtitle) = Film(folder);

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly));

        Assert.Equal(inSync, result.AlreadyInSync);
        Assert.Equal(!inSync, File.Exists(Path.Combine(folder, "Film.en.shifted.srt")));
    }

    [Fact]
    public async Task ADoubtfulMoveIsMarkedAndDoesNotGoOverAFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":2500,\"ratio\":1,\"verdict\":\"unsure\",\"parts\":1,\"written\":true,\"output\":\"x\",\"splits\":[],\"offsets\":[2500]}");
        var (video, subtitle) = Film(folder);
        var taken = Path.Combine(folder, "Copy.en.srt");
        File.WriteAllText(taken, "keep me");

        var result = await WithSettings(engine, () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly, taken, fromVideoPath: Path.Combine(folder, "Other.mkv")));

        Assert.True(result.Unconfirmed);
        Assert.False(result.CountsAsSynced);
        Assert.Equal(Path.Combine(folder, "Copy.en.shifted.srt"), result.OutputPath);
        Assert.Equal("keep me", File.ReadAllText(taken));
    }

    [Fact]
    public async Task WhisperGetsTheModelAndWhereWhisperCliIs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (runner, folder, engine) = NewRunner("{\"mode\":\"auto/shifted\",\"reference\":\"vad+whisper\",\"offset_ms\":900,\"ratio\":1,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"output\":\"x\",\"splits\":[],\"offsets\":[900]}");
        var (video, subtitle) = Film(folder);
        var model = Path.Combine(folder, "ggml-base.en.bin");
        var cli = Path.Combine(folder, "whisper-cli");
        File.WriteAllText(model, "model");
        File.WriteAllText(cli, "cli");

        var result = await WithSettings(
            engine,
            () => runner.RunAsync(new LapseEngine(), video, subtitle, SyncMode.Auto, 6, OutputMode.SidecarOnly),
            (WhisperSetup.ModelKey, model),
            (WhisperSetup.CliKey, cli));

        Assert.True(result.Success, result.Error);
        Assert.Equal("vad+whisper", result.Reference);
        Assert.Contains("--whisper " + model, Calls(engine), StringComparison.Ordinal);
        Assert.Contains("env LAPSE_WHISPER_CLI=" + cli, Calls(engine), StringComparison.Ordinal);
    }

    internal static (EngineRunner Runner, string Folder, string Engine) NewRunner(string reply, string? usage = null)
    {
        var folder = TestHost.NewFolder();
        var engine = Path.Combine(folder, "lapse");
        File.WriteAllText(engine, FakeLapse.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(engine + ".reply", reply + "\n");

        if (usage is not null)
        {
            File.WriteAllText(engine + ".usage", usage + "\n");
        }

        _ = TestHost.Plugin;
        var encoder = NothingProxy.Create<IMediaEncoder>();
        var runner = new EngineRunner(
            TestHost.ApplicationPaths,
            new EngineCapabilityProbe(NullLogger<EngineCapabilityProbe>.Instance),
            encoder,
            new SubtitleConverter(encoder, NullLogger<SubtitleConverter>.Instance),
            NullLogger<EngineRunner>.Instance);

        return (runner, folder, engine);
    }

    internal static (string Video, string Subtitle) Film(string folder)
    {
        var video = Path.Combine(folder, "Film.mkv");
        File.WriteAllText(video, "not really a film");

        var subtitle = Path.Combine(folder, "Film.en.srt");
        File.WriteAllText(subtitle, "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nThere\n");
        return (video, subtitle);
    }

    // Points LAPSE at the fake, turns the skip on and sets any parameters, then puts it all back.
    internal static async Task<SyncResult> WithSettings(string engine, Func<Task<SyncResult>> run, params (string Key, string? Value)[] parameters)
    {
        var config = TestHost.Plugin.Configuration;
        var settings = config.GetEngineSettings("lapse");
        var path = settings.PathOverride;
        var skip = config.SkipAlreadyInSync;
        var tolerance = config.AlreadyInSyncToleranceMs;
        var lowConfidence = config.LowConfidenceAction;
        var saved = settings.GetParameterMap().ToList();

        try
        {
            settings.PathOverride = engine;
            config.SkipAlreadyInSync = true;
            config.AlreadyInSyncToleranceMs = 100;
            config.LowConfidenceAction = LowConfidenceAction.Sidecar;
            settings.SetParameters(parameters.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
            return await run();
        }
        finally
        {
            settings.PathOverride = path;
            config.SkipAlreadyInSync = skip;
            config.AlreadyInSyncToleranceMs = tolerance;
            config.LowConfidenceAction = lowConfidence;
            settings.SetParameters(saved);
        }
    }

    private static string Calls(string engine)
    {
        return File.ReadAllText(engine + ".calls");
    }

    /// <summary>
    /// Answers every call with nothing, for services the runner only touches in passing.
    /// </summary>
    public class NothingProxy : DispatchProxy
    {
        public static T Create<T>()
            where T : class => Create<T, NothingProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod?.ReturnType;
            return type is not null && type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
