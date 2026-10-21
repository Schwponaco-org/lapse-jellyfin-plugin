// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// What LAPSE 2.3.0 added: moving a subtitle between copies with --from, whisper, the
/// offset of every part of a split file, and naming a subtitle moved onto another copy.
/// </summary>
public class Lapse23Tests
{
    public Lapse23Tests()
    {
        _ = TestHost.Plugin;
    }

    private static EngineRuntimeInfo Runtime(params string[] flags)
    {
        var runtime = new EngineRuntimeInfo { Probed = true, UsageText = "lapse <video> [subtitle] [auto|ols|nosplit|split]" };
        runtime.Flags.AddRange(flags);
        return runtime;
    }

    private static EngineRunOptions Options(EngineRuntimeInfo runtime, EngineParameterValues values, string? from = null)
    {
        return new EngineRunOptions
        {
            ReferencePath = "/m/Film - Bluray.mkv",
            InputPath = "/m/Film - TV.en.srt",
            OutputPath = "/m/Film - TV.en.srt.lapse-tmp.srt",
            Mode = SyncMode.Auto,
            Runtime = runtime,
            Parameters = values,
            FromVideoPath = from
        };
    }

    private static EngineParameterValues Values(params (string Key, string? Value)[] saved)
    {
        return new EngineParameterValues(new LapseEngine().Descriptor.Parameters, saved.ToDictionary(s => s.Key, s => s.Value));
    }

    private static string Touch(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void AMoveBetweenCopiesComparesSpeechWithSpeech()
    {
        var engine = new LapseEngine();
        var current = engine.BuildArguments(Options(Runtime("--output", "--json", "--from", "--no-embedded"), Values(), "/m/Film - TV.mkv"));
        var old = engine.BuildArguments(Options(Runtime("--output", "--json", "--no-embedded"), Values(), "/m/Film - TV.mkv"));
        var plain = engine.BuildArguments(Options(Runtime("--output", "--json", "--from", "--no-embedded"), Values()));

        Assert.Equal("/m/Film - TV.mkv", current[current.ToList().IndexOf("--from") + 1]);
        Assert.Contains("--no-embedded", current);
        Assert.DoesNotContain("--from", old);
        Assert.DoesNotContain("--from", plain);
        Assert.DoesNotContain("--no-embedded", plain);
    }

    [Fact]
    public void WhisperIsOnlyPassedOnceItCanRun()
    {
        var folder = TestHost.NewFolder();
        var model = Touch(folder, "ggml-base.en.bin");
        var cli = Touch(folder, "whisper-cli");
        var engine = new LapseEngine();
        var runtime = Runtime("--output", "--json", "--whisper");

        var ready = Values((WhisperSetup.ModelKey, model), (WhisperSetup.CliKey, cli));
        var args = engine.BuildArguments(Options(runtime, ready));
        Assert.Equal(model, args[args.ToList().IndexOf("--whisper") + 1]);
        Assert.Equal(cli, engine.GetEnvironment(ready)[WhisperSetup.CliVariable]);

        // an older build, nothing set, a name LAPSE can't read, a missing file, no whisper-cli
        Assert.DoesNotContain("--whisper", engine.BuildArguments(Options(Runtime("--output", "--json"), ready)));
        Assert.DoesNotContain("--whisper", engine.BuildArguments(Options(runtime, Values())));
        Assert.False(WhisperSetup.Check(Values()).Configured);

        var renamed = Values((WhisperSetup.ModelKey, Touch(folder, "english.bin")), (WhisperSetup.CliKey, cli));
        Assert.DoesNotContain("--whisper", engine.BuildArguments(Options(runtime, renamed)));
        Assert.Contains("ggml-base.en.bin", WhisperSetup.Check(renamed).Problem, StringComparison.Ordinal);

        var missing = Values((WhisperSetup.ModelKey, Path.Combine(folder, "ggml-small.bin")), (WhisperSetup.CliKey, cli));
        Assert.DoesNotContain("--whisper", engine.BuildArguments(Options(runtime, missing)));
        Assert.Contains("no whisper model", WhisperSetup.Check(missing).Problem, StringComparison.Ordinal);

        var noCli = Values((WhisperSetup.ModelKey, model), (WhisperSetup.CliKey, Path.Combine(folder, "nope")));
        Assert.DoesNotContain("--whisper", engine.BuildArguments(Options(runtime, noCli)));
        Assert.Empty(engine.GetEnvironment(noCli));
    }

    [Theory]
    [InlineData("ggml-base.en.bin", true)]
    [InlineData("/models/ggml-large-v3-turbo.bin", true)]
    [InlineData("ggml-tiny.bin", true)]
    [InlineData("ggml-base.en-q5_1.bin", false)]
    [InlineData("whisper.bin", false)]
    public void WhisperModelsAreKnownByTheirName(string path, bool known)
    {
        Assert.Equal(known, WhisperSetup.IsKnownModelName(path));
    }

    [Fact]
    public void EveryPartOfASplitFileHasItsOwnOffset()
    {
        var stdout = "{\"mode\":\"auto/shifted+split\",\"reference\":\"vad+whisper\",\"offset_ms\":-4917,\"ratio\":1,\"confidence\":0.7,"
            + "\"sigma\":11.9,\"agreement\":0.5,\"verdict\":\"solid\",\"coverage\":1,\"cues\":86,\"ignored_cues\":3,\"parts\":2,"
            + "\"snapped\":0,\"written\":true,\"unchanged\":false,\"output\":\"/x.srt\",\"splits\":[44],\"offsets\":[-892,-4892]}";

        var result = new LapseEngine().ParseResult(stdout, string.Empty, 0, SyncMode.Auto, 6);

        Assert.Equal(SyncMode.Split, result.Mode);
        Assert.Equal(new[] { -892, -4892 }, result.PartOffsetsMs);
        Assert.Equal(3, result.IgnoredCues);
        Assert.Equal("vad+whisper", result.Reference);
        Assert.Contains("whisper heard words", result.EngineOutput, StringComparison.Ordinal);
        Assert.Contains("3 of them left out", result.EngineOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeFileHasNoPartOffsets()
    {
        var stdout = "{\"mode\":\"auto/shifted\",\"offset_ms\":22,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"offsets\":[22]}";
        Assert.Null(new LapseEngine().ParseResult(stdout, string.Empty, 0, SyncMode.Auto, 6).PartOffsetsMs);
    }

    [Theory]
    [InlineData("/m/Film - TV.en.srt", null, ".en")]
    [InlineData("/m/Film - TV.en.forced.srt", null, ".en.forced")]
    [InlineData("/m/Film - TV.eng.stream2.srt", "eng", ".eng")]
    [InlineData("/m/Film - TV.eng.forced.stream4.srt", "eng", ".eng.forced")]
    [InlineData("/m/Film - TV.en.shifted.srt", null, ".en")]
    [InlineData("/m/Film - TV.srt", null, "")]
    [InlineData("/m/Subs/English.srt", "eng", ".eng")]
    [InlineData("/m/Subs/English.srt", null, "")]
    public void AMovedSubtitleKeepsItsLanguageAndFlags(string subtitle, string? language, string tail)
    {
        Assert.Equal(tail, CopiedSubtitleNames.Tail("/m/Film - TV.mkv", subtitle, language));
    }

    [Fact]
    public void AMovedSubtitleNeverGoesOverOneThatIsThere()
    {
        var folder = TestHost.NewFolder();
        var target = Path.Combine(folder, "Film - Bluray.mkv");
        var source = Path.Combine(folder, "Film - TV.mkv");

        Assert.Equal(Path.Combine(folder, "Film - Bluray.en.srt"), CopiedSubtitleNames.ResolveDestination(target, source, Path.Combine(folder, "Film - TV.en.srt"), null, null));
        Assert.Equal(Path.Combine(folder, "Film - Bluray.en.srt"), CopiedSubtitleNames.ResolveDestination(target, source, Path.Combine(folder, "Film - TV.en.sub"), null, "srt"));

        Touch(folder, "Film - Bluray.en.srt");
        Assert.Equal(Path.Combine(folder, "Film - Bluray.en.shifted.srt"), CopiedSubtitleNames.ResolveDestination(target, source, Path.Combine(folder, "Film - TV.en.srt"), null, null));
    }
}
