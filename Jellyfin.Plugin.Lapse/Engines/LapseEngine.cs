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
/// The LAPSE engine. CLI:
///
/// lapse &lt;video_or_subtitle&gt; &lt;subtitle&gt; [auto|ols|nosplit|split] [penalty]
///       [--output PATH] [--confidence N] [--audio-track N] [--sub-track N]
///       [--fps N] [--snap [ms]] [--encoding NAME]
///       [--no-backup] [--no-sidecar] [--no-embedded] [--full-scan] [--no-cache]
///       [--force] [--strict] [--dry-run] [--json] [--quiet]
///
/// --json gives one line of JSON with the verdict, much better than scraping text. Old
/// builds don't have it (or auto mode), so the text parsing stays for those.
/// </summary>
public partial class LapseEngine : IEngine
{
    /// <summary>
    /// The engine's default --confidence (sure_sigma in its main.cpp), in standard deviations.
    /// </summary>
    public const double DefaultConfidenceSigma = 8.0;

    private const string RepoUrl = "https://github.com/Schwponaco-org/lapse";
    private const string BenchmarksUrl = "https://github.com/Schwponaco-org/lapse/blob/main/docs/benchmarks.md";

    // numeric parameters and the flag each one drives
    private static readonly (string Key, string Flag)[] NumberFlags =
    {
        ("audioTrack", "--audio-track"),
        ("subTrack", "--sub-track"),
        ("fps", "--fps"),
        ("snap", "--snap")
    };

    // Ship in the archive and have to sit next to the binary. Without them silero quietly
    // falls back to the weaker voice detector and everything syncs worse.
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
        // without --output it edits in place, so it needs a copy to chew on
        return !runtime.SupportsOutputFlag;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(EngineRunOptions options)
    {
        var runtime = options.Runtime;
        var values = options.Parameters;
        var supportsOutput = runtime.SupportsOutputFlag;

        // without --output we point it at the copy the runner made
        var subtitleArgument = supportsOutput ? options.InputPath : options.OutputPath;

        var mode = options.Mode;
        if (mode == SyncMode.Auto && !runtime.SupportsAutoMode)
        {
            // old builds reject "auto", single offset is what they defaulted to anyway
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

        // positional, right after the mode, split only (auto works it out itself)
        if (mode == SyncMode.Split)
        {
            args.Add(options.Penalty.ToString(CultureInfo.InvariantCulture));
        }

        if (supportsOutput)
        {
            args.Add("--output");
            args.Add(options.OutputPath);
        }

        // we do our own backups. Old builds write a .bak anyway, the runner cleans it up
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

        // only flags the installed build lists, old builds refuse unknown ones
        foreach (var (key, flag) in NumberFlags)
        {
            if (runtime.HasFlag(flag))
            {
                values.AddNumber(args, key, flag);
            }
        }

        if (runtime.HasFlag("--encoding"))
        {
            values.AddText(args, "encoding", "--encoding");
        }

        // a track extracted from this video must go against the audio, and the runner asks
        // for --force on the retry after "too few cues"
        AddIfSupported(args, runtime, "--no-embedded", values.GetBool("noEmbedded") || options.IgnoreEmbedded);
        AddIfSupported(args, runtime, "--full-scan", values.GetBool("fullScan"));
        AddIfSupported(args, runtime, "--no-cache", values.GetBool("noCache"));
        AddIfSupported(args, runtime, "--force", values.GetBool("force") || options.ForceAnyway);

        return args;
    }

    /// <inheritdoc />
    public SyncResult ParseResult(string stdout, string stderr, int exitCode, SyncMode requestedMode, int requestedPenalty)
    {
        // JSON report if there is one, could be on either stream
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
            GitHubRepo = "Schwponaco-org/lapse",
            BuildGuideUrl = RepoUrl + "#building",
            ExecutableName = "lapse",
            Tier = EngineTier.Recommended,
            AdvancedNote = "Text encoding needs no setting here for the usual case. LAPSE detects the "
                + "encoding of the subtitle it reads and writes the result back in the same one, so a "
                + "Windows-1252 file stays Windows-1252 and a UTF-8 file stays UTF-8. The encoding picker "
                + "is for the other case: a library that is a mix of UTF-16 and old codepage files, which "
                + "you would rather were all one thing. The other two engines take encoding arguments "
                + "because they cannot work it out for themselves.",
            WhyUrl = BenchmarksUrl,
            WhyLabel = "Why is this recommended? Read more on GitHub",
            DeploymentNote = "LAPSE also ships as a Docker image with a file watcher in it. Point it at "
                + "the folders you want it to look after and it syncs new files on its own, with no Jellyfin "
                + "involved. There is a web UI too, for anyone who would rather manage it from a browser than "
                + "the command line. Useful if you want subtitles fixed before Jellyfin ever sees them, or if "
                + "you keep media that isn't in a Jellyfin library. Setup and settings are in the repo.",
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
                MaxPenalty = 100,
                SubtitleExtensions = EngineFormats.Lapse
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
            Key = "fps",
            Label = "Frame rate for MicroDVD subtitles",
            Description = "MicroDVD .sub files count frames rather than time, so they need a frame rate. The engine reads it from the video, or from the file's own header when there is one, and only needs telling when neither is there. Blank leaves it to work that out.",
            Flag = "--fps",
            Kind = EngineParameterKind.Number,
            Minimum = 10,
            Maximum = 120,
            Step = 0.001,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "snap",
            Label = "Snap cue starts onto picture cuts (ms)",
            Description = "A subtitler writing to picture puts a line up on the cut, so a file moved by "
                + "one number can be right to within a few frames and still read late all the way through. "
                + "This pulls a cue start that lands within the given window onto the cut itself, and moves "
                + "the cue's end with it so the line stays up just as long. The cuts come from the keyframes "
                + "already in the video, which costs an index read and no decoding. Blank leaves it off; 120 "
                + "is the engine's own window. It does nothing when the reference is another subtitle rather "
                + "than a video, or when the video was encoded without scene detection.",
            Flag = "--snap",
            Kind = EngineParameterKind.Number,
            Minimum = 0,
            Maximum = 1000,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "encoding",
            Label = "Write every result in one encoding",
            Description = "A subtitle goes back out in whatever it came in as unless this is set, which is "
                + "what you want almost every time. Pick an encoding and every result is written that way "
                + "instead, which is the quick way to get a library of mixed UTF-16 and codepage files down "
                + "to one. The engine cannot tell one ASCII compatible codepage from another, so converting "
                + "one to UTF-8 reads it as ISO-8859-1: right for Western European subtitles, wrong for "
                + "Cyrillic and CJK, where the result is valid UTF-8 with the wrong letters in it. .sup and "
                + ".idx hold pictures rather than text and are left alone either way.",
            Flag = "--encoding",
            Kind = EngineParameterKind.Select,
            BlankMeansUnset = true
        });
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption(string.Empty, "Keep what each file came in as (default)"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("utf8", "UTF-8"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("utf8-bom", "UTF-8 with a byte order mark"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("utf16le", "UTF-16 little endian"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("utf16be", "UTF-16 big endian"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("latin1", "ISO-8859-1 (Latin-1)"));

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

    private static void AddIfSupported(List<string> args, EngineRuntimeInfo runtime, string flag, bool wanted)
    {
        if (wanted && runtime.HasFlag(flag))
        {
            args.Add(flag);
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
                OffsetMs = ReadDouble(root, "offset_ms") is { } offset ? (int)Math.Round(offset) : null,
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

            var snapped = ReadDouble(root, "snapped");
            if (snapped is > 0)
            {
                result.Snapped = (int)snapped.Value;
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

    // auto mode reports what it actually did ("auto/shifted", "auto/drifting", "auto/recut"...)
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
