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
            echo "Usage: lapse <video_or_subtitle> [subtitle] [auto|ols|nosplit|split] [penalty] [--output <path>] [--no-backup] [--no-sidecar] [--no-embedded] [--full-scan] [--no-cache] [--force] [--json] [--quiet] [--dry-run] [--strict] [--confidence N] [--audio-track N] [--sub-track N] [--fps N] [--snap [ms]] [--encoding NAME] [--from video] [--min-shift ms] [--whisper model]" >&2
            exit 255 ;;
          *)
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

    internal static (EngineRunner Runner, string Folder, string Engine) NewRunner(string reply)
    {
        var folder = TestHost.NewFolder();
        var engine = Path.Combine(folder, "lapse");
        File.WriteAllText(engine, FakeLapse.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(engine + ".reply", reply + "\n");

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

    // Points LAPSE at the fake and turns the skip on, then puts both back.
    internal static async Task<SyncResult> WithSettings(string engine, Func<Task<SyncResult>> run)
    {
        var config = TestHost.Plugin.Configuration;
        var settings = config.GetEngineSettings("lapse");
        var path = settings.PathOverride;
        var skip = config.SkipAlreadyInSync;
        var tolerance = config.AlreadyInSyncToleranceMs;

        try
        {
            settings.PathOverride = engine;
            config.SkipAlreadyInSync = true;
            config.AlreadyInSyncToleranceMs = 100;
            return await run();
        }
        finally
        {
            settings.PathOverride = path;
            config.SkipAlreadyInSync = skip;
            config.AlreadyInSyncToleranceMs = tolerance;
        }
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
