// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// The LAPSE engine, which is what this plugin is built around. Its CLI is:
///
/// lapse &lt;video_or_subtitle&gt; &lt;subtitle&gt; [auto|ols|nosplit|split] [penalty]
///       [--output PATH] [--confidence N] [--audio-track N] [--sub-track N]
///       [--no-backup] [--no-sidecar] [--no-embedded] [--full-scan] [--no-cache]
///       [--force] [--strict] [--dry-run] [--json] [--quiet]
///
/// With --json it prints one line of JSON describing what it did, including the verdict
/// it reached, which is a much better thing to judge a run on than scraping a sentence.
/// Older builds don't have --json (or the auto mode), so the runtime probe decides which
/// of the two paths to take and the plain-text parsing is kept for those.
/// </summary>
public partial class LapseEngine : IEngine
{
    /// <summary>
    /// The engine's own default for --confidence: how many standard deviations the answer
    /// has to stand out by before it will overwrite the original. This is sure_sigma in
    /// the engine's main.cpp, and the plugin's own threshold setting defaults to it rather
    /// than to a number of the plugin's own invention.
    /// </summary>
    public const double DefaultConfidenceSigma = 8.0;

    private const string RepoUrl = "https://github.com/rs-jensen/lapse";
    private const string BenchmarksUrl = "https://github.com/rs-jensen/lapse/blob/main/docs/benchmarks.md";

    // The files that ship in the same archive as the binary and have to land beside it.
    // silero.cpp's loader looks for these next to the executable and quietly falls back to
    // the weaker built-in voice detection when they aren't there, so a "successful" install
    // that dropped them would just sync worse for no visible reason.
    private static readonly string[] CompanionNames =
    {
        "libonnxruntime.so", "libonnxruntime.dylib", "onnxruntime.dll", "silero_vad.onnx"
    };

    // Done (nosplit): offset=1250ms -> /path/to/out.srt
    [GeneratedRegex(@"^Done \((?<mode>[^)]*)\): offset=(?<offset>-?[0-9]+)ms(?<extra>.*?) -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex TextOutputRegex();

    // confidence=0.82, wherever it turns up on the line
    [GeneratedRegex(@"confidence=(?<confidence>[0-9.eE+-]+)")]
    private static partial Regex ConfidenceRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = BuildDescriptor();

    /// <inheritdoc />
    public bool NeedsSeededOutput(EngineRuntimeInfo runtime)
    {
        // With --output it writes wherever we point it. Without, it edits in place and the
        // only way to get the result somewhere else is to give it a copy to work on.
        return !runtime.SupportsOutputFlag;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(EngineRunOptions options)
    {
        var runtime = options.Runtime;
        var values = options.Parameters;
        var supportsOutput = runtime.SupportsOutputFlag;

        // Without --output, LAPSE rewrites the file it's handed, so the caller copies the
        // input to the output path first and we point LAPSE at that copy.
        var subtitleArgument = supportsOutput ? options.InputPath : options.OutputPath;

        var mode = options.Mode;
        if (mode == SyncMode.Auto && !runtime.SupportsAutoMode)
        {
            // An older binary would reject "auto" outright. Its nearest equivalent is the
            // plain single-offset search, which is what it used to default to anyway.
            mode = SyncMode.Standard;
        }

        var modeArgument = mode switch
        {
            SyncMode.Auto => "auto",
            SyncMode.Ols => "ols",
            SyncMode.Split => "split",
            _ => "nosplit"
        };

        var args = new List<string> { options.ReferencePath, subtitleArgument, modeArgument };

        // The penalty is positional and has to come right after the mode. Only split mode
        // reads it; in auto mode the engine works the penalty out from the size of the
        // file, which it does better than a fixed number would.
        if (mode == SyncMode.Split)
        {
            args.Add(options.Penalty.ToString(CultureInfo.InvariantCulture));
        }

        if (supportsOutput)
        {
            args.Add("--output");
            args.Add(options.OutputPath);
        }

        // The plugin does its own backups, driven by the output mode setting, so let the
        // engine skip its .bak when it knows how. Older builds always write one, next to
        // the temporary work file, and the runner cleans that up afterwards.
        if (runtime.SupportsNoBackupFlag)
        {
            args.Add("--no-backup");
        }

        if (runtime.HasFlag("--json"))
        {
            args.Add("--json");
        }

        if (runtime.HasFlag("--confidence"))
        {
            args.Add("--confidence");
            args.Add(EngineParameterValues.Format(options.ConfidenceSigma));
        }

        AddNumber(args, runtime, values, "audioTrack", "--audio-track");
        AddNumber(args, runtime, values, "subTrack", "--sub-track");

        AddSwitch(args, runtime, values, "noEmbedded", "--no-embedded");
        AddSwitch(args, runtime, values, "fullScan", "--full-scan");
        AddSwitch(args, runtime, values, "noCache", "--no-cache");
        AddSwitch(args, runtime, values, "force", "--force");

        return args;
    }

    /// <inheritdoc />
    public SyncResult ParseResult(string stdout, string stderr, int exitCode, SyncMode requestedMode, int requestedPenalty)
    {
        // The JSON report is the reliable source when the binary has it. It's a single
        // line on stdout, but be generous about where it is in case something else got
        // printed first.
        var report = ReadJsonReport(stdout) ?? ReadJsonReport(stderr);
        if (report is not null)
        {
            return report;
        }

        if (exitCode != 0)
        {
            return EngineResults.Failure(requestedMode, stderr, exitCode);
        }

        var text = stdout + "\n" + stderr;
        var match = TextOutputRegex().Match(text);
        if (match.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = ModeFromEngineName(match.Groups["mode"].Value, requestedMode),
                OffsetMs = int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture),
                Confidence = ReadConfidence(match.Value),
                EngineOutput = EngineResults.Summarize(text)
            };
        }

        return new SyncResult
        {
            Success = false,
            Mode = requestedMode,
            Error = "Engine finished but its output didn't look like a normal result. Check the server logs."
        };
    }

    private static EngineDescriptor BuildDescriptor()
    {
        var descriptor = new EngineDescriptor
        {
            Id = "lapse",
            DisplayName = "LAPSE",
            Description = "Lines subtitles up against the speech in the audio, so it works whatever "
                + "language either of them is in. Works out on its own whether the file is shifted, "
                + "stretched, split or re-cut, and says how sure it is about the answer.",
            ProjectUrl = RepoUrl,
            GitHubRepo = "rs-jensen/lapse",
            BuildGuideUrl = RepoUrl + "#building",
            ExecutableName = "lapse",
            EditsInPlace = true,
            Tier = EngineTier.Recommended,
            AdvancedNote = "There is nothing here for text encoding, and there does not need to be. "
                + "LAPSE detects the encoding of the subtitle it reads and writes the result back in the "
                + "same one, so a Windows-1252 file stays Windows-1252 and a UTF-8 file stays UTF-8. "
                + "The other two engines take encoding arguments because they do not do this.",
            WhyUrl = BenchmarksUrl,
            WhyLabel = "Why is this recommended? Read more on GitHub",
            LinuxAmd64 = Archive("lapse-linux-amd64.tar.gz", EnginePackaging.TarGz),
            LinuxArm64 = Archive("lapse-linux-arm64.tar.gz", EnginePackaging.TarGz),
            MacAmd64 = Archive("lapse-macos-x86_64.tar.gz", EnginePackaging.TarGz),
            MacArm64 = Archive("lapse-macos-arm64.tar.gz", EnginePackaging.TarGz),
            WindowsAmd64 = Archive("lapse-windows-x64.zip", EnginePackaging.Zip),
            Capabilities = new EngineCapabilities
            {
                SupportsStandard = true,
                SupportsAuto = true,
                SupportsOls = true,
                SupportsSplit = true,
                SupportsPenalty = true,
                DefaultPenalty = 6,
                MinPenalty = 0,
                MaxPenalty = 100
            }
        };

        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Auto,
            "Auto",
            "Picks whichever of the others fits. Leave it here."));
        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Standard,
            "Single offset",
            "Moves the whole file by one amount. For subtitles that are simply early or late."));
        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Split,
            "Split",
            "Gives different parts of the file different timing. For discs cut around ad breaks, or a film joined from two rips."));
        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Ols,
            "Framerate fix",
            "Stretches the file so it drifts less as it plays. For a subtitle made for a 25fps release played against a 23.976fps one, where the gap grows from nothing to minutes by the end."));

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "audioTrack",
            Label = "Audio track",
            Description = "Which audio stream to listen to, counting from 0. Blank lets the engine pick, which is normally what you want.",
            Flag = "--audio-track",
            Kind = EngineParameterKind.Number,
            Minimum = 0,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "subTrack",
            Label = "Embedded subtitle track",
            Description = "Which subtitle stream inside the video to use as the reference, counting from 0. Blank lets the engine pick.",
            Flag = "--sub-track",
            Kind = EngineParameterKind.Number,
            Minimum = 0,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "noEmbedded",
            Label = "Ignore embedded subtitles",
            Description = "Always listen to the audio instead of lining up against a subtitle track already inside the video. Slower, but right when the embedded track is itself out of sync.",
            Flag = "--no-embedded",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "fullScan",
            Label = "Listen to the whole file",
            Description = "Decode all the audio instead of sampling the first stretch of it. Slower, and worth it on films that open with a long silent or musical passage.",
            Flag = "--full-scan",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "noCache",
            Label = "Do not reuse the speech cache",
            Description = "The engine saves where it found speech so other tracks for the same video start from the answer. Turn this off only when you suspect a stale cache.",
            Flag = "--no-cache",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "force",
            Label = "Sync even when the engine is unsure",
            Description = "Treats every result as confident, and syncs files with too few cues to judge properly. Off by default, and the safer place to handle this is the low confidence setting under File output.",
            Flag = "--force",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        return descriptor;
    }

    private static EngineDownload Archive(string assetName, EnginePackaging packaging)
    {
        var download = new EngineDownload(
            $"{RepoUrl}/releases/latest/download/{assetName}",
            packaging);

        download.CompanionFiles.AddRange(CompanionNames);
        return download;
    }

    private static void AddSwitch(
        List<string> args,
        EngineRuntimeInfo runtime,
        EngineParameterValues values,
        string key,
        string flag)
    {
        if (values.GetBool(key) && runtime.HasFlag(flag))
        {
            args.Add(flag);
        }
    }

    private static void AddNumber(
        List<string> args,
        EngineRuntimeInfo runtime,
        EngineParameterValues values,
        string key,
        string flag)
    {
        var value = values.GetInt(key);
        if (value.HasValue && runtime.HasFlag(flag))
        {
            args.Add(flag);
            args.Add(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    // { "mode":"auto/shifted", "offset_ms":1250, "confidence":0.82, "verdict":"solid",
    //   "written":true, "output":"/path/to.srt", ... }
    private static SyncResult? ReadJsonReport(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("verdict", out _))
            {
                return null;
            }

            var verdict = ReadString(root, "verdict") ?? "nothing";
            var written = root.TryGetProperty("written", out var writtenElement)
                && writtenElement.ValueKind == JsonValueKind.True;

            var result = new SyncResult
            {
                Success = written,
                Mode = ModeFromEngineName(ReadString(root, "mode"), SyncMode.Auto),
                Verdict = verdict,
                Confidence = ReadDouble(root, "confidence"),
                Sigma = ReadDouble(root, "sigma"),
                Agreement = ReadDouble(root, "agreement"),
                OffsetMs = (int?)ReadDouble(root, "offset_ms"),
                EngineOutput = DescribeReport(root, verdict)
            };

            var ratio = ReadDouble(root, "ratio");
            if (ratio.HasValue && Math.Abs(ratio.Value - 1.0) > 0.000001)
            {
                result.Slope = ratio.Value - 1.0;
                result.Intercept = (result.OffsetMs ?? 0) / 1000.0;
            }

            var parts = ReadDouble(root, "parts");
            if (parts is > 1)
            {
                result.Penalty = (int)parts.Value;
            }

            if (!written)
            {
                result.Error = ReadString(root, "why") ?? "The engine decided not to write anything.";
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeReport(JsonElement root, string verdict)
    {
        var cues = ReadDouble(root, "cues");
        var coverage = ReadDouble(root, "coverage");
        var reference = ReadString(root, "reference");

        var parts = new List<string> { "verdict " + verdict };

        if (reference is not null)
        {
            parts.Add("matched against " + (reference == "vad" ? "the audio" : reference));
        }

        if (cues.HasValue)
        {
            parts.Add(EngineParameterValues.Format(cues.Value) + " cues");
        }

        if (coverage is < 1.0)
        {
            parts.Add("heard " + Math.Round(coverage.Value * 100) + "% of the file");
        }

        return string.Join(", ", parts);
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static double? ReadDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;
    }

    // The engine reports what it actually did, which in auto mode is one of "auto/shifted",
    // "auto/drifting", "auto/recut" and friends rather than the mode we asked for.
    private static SyncMode ModeFromEngineName(string? name, SyncMode fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        if (name.Contains("split", StringComparison.OrdinalIgnoreCase)
            || name.Contains("recut", StringComparison.OrdinalIgnoreCase)
            || name.Contains("joined", StringComparison.OrdinalIgnoreCase)
            || name.Contains("restart", StringComparison.OrdinalIgnoreCase))
        {
            return SyncMode.Split;
        }

        if (name.Contains("ols", StringComparison.OrdinalIgnoreCase)
            || name.Contains("drifting", StringComparison.OrdinalIgnoreCase))
        {
            return SyncMode.Ols;
        }

        return name.Contains("auto", StringComparison.OrdinalIgnoreCase) ? SyncMode.Auto : SyncMode.Standard;
    }

    private static double? ReadConfidence(string line)
    {
        var match = ConfidenceRegex().Match(line);
        if (match.Success && double.TryParse(match.Groups["confidence"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }
}
