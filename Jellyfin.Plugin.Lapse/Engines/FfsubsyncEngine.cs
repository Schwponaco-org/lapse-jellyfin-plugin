// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// ffsubsync by smacke. Its CLI is:
///
/// ffsubsync &lt;reference&gt; -i &lt;input&gt; -o &lt;output&gt;
///           [--split-penalty N] [--max-offset-seconds N] [--vad NAME] [--gss]
///           [--no-fix-framerate] [--frame-rate N] [--encoding E] [--output-encoding E]
///           [--max-subtitle-seconds N] [--start-seconds N]
///           [--reference-stream S] [--ffmpeg-path DIR] [--strict]
///
/// Names and defaults come from the argument parser in ffsubsync/ffsubsync.py. It works
/// out one offset for the whole file by default; giving it --split-penalty turns on its
/// piecewise mode, which is the closest thing it has to split alignment.
/// </summary>
public partial class FfsubsyncEngine : IEngine
{
    private const string RepoUrl = "https://github.com/smacke/ffsubsync";

    // ffsubsync logs a line with the offset it settled on. Same caveat as alass, this is
    // best effort and the run is really judged on exit code plus output file.
    [GeneratedRegex(@"offset\s*(?:seconds)?\s*[:=]\s*(?<seconds>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = BuildDescriptor();

    /// <inheritdoc />
    public bool NeedsSeededOutput(EngineRuntimeInfo runtime) => false;

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(EngineRunOptions options)
    {
        var values = options.Parameters;
        var args = new List<string> { options.ReferencePath, "-i", options.InputPath, "-o", options.OutputPath };

        // ffsubsync runs ffmpeg and ffprobe itself. Telling it exactly which folder to
        // look in is steadier than hoping PATH resolves to the same build, since the
        // Jellyfin images keep their ffmpeg somewhere PATH doesn't cover.
        if (!string.IsNullOrEmpty(options.FfmpegDirectory))
        {
            args.Add("--ffmpeg-path");
            args.Add(options.FfmpegDirectory);
        }

        // The presence of --split-penalty is what switches ffsubsync into piecewise mode,
        // so it only goes on the command line for a split run.
        if (options.Mode == SyncMode.Split)
        {
            args.Add("--split-penalty");
            args.Add(options.Penalty.ToString(CultureInfo.InvariantCulture));
        }

        AddNumber(args, values, "maxOffsetSeconds", "--max-offset-seconds");
        AddNumber(args, values, "maxSubtitleSeconds", "--max-subtitle-seconds");
        AddNumber(args, values, "startSeconds", "--start-seconds");
        AddNumber(args, values, "frameRate", "--frame-rate");
        AddText(args, values, "vad", "--vad");
        AddText(args, values, "encoding", "--encoding");
        AddText(args, values, "outputEncoding", "--output-encoding");
        AddText(args, values, "referenceStream", "--reference-stream");

        if (values.GetBool("gss"))
        {
            args.Add("--gss");
        }

        if (values.GetBool("noFixFramerate"))
        {
            args.Add("--no-fix-framerate");
        }

        if (values.GetBool("strict"))
        {
            args.Add("--strict");
        }

        return args;
    }

    /// <inheritdoc />
    public SyncResult ParseResult(string stdout, string stderr, int exitCode, SyncMode requestedMode, int requestedPenalty)
    {
        if (exitCode != 0)
        {
            return EngineResults.Failure(requestedMode, stderr, exitCode);
        }

        var result = new SyncResult
        {
            Success = true,
            Mode = requestedMode,
            Penalty = requestedMode == SyncMode.Split ? requestedPenalty : null,

            // ffsubsync does most of its talking on stderr, so look there first
            EngineOutput = EngineResults.Summarize(stderr) ?? EngineResults.Summarize(stdout)
        };

        var combined = stdout + "\n" + stderr;
        var match = OffsetRegex().Match(combined);
        if (match.Success && double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            result.OffsetMs = (int)(seconds * 1000);
        }

        return result;
    }

    private static EngineDescriptor BuildDescriptor()
    {
        var descriptor = new EngineDescriptor
        {
            Id = "ffsubsync",
            DisplayName = "ffsubsync",
            Description = "Shifts the whole subtitle to match the speech in the audio and can correct a "
                + "framerate mismatch. Has a piecewise mode when it is given a split penalty, and reports "
                + "no confidence score.",
            ProjectUrl = RepoUrl,
            GitHubRepo = "smacke/ffsubsync",
            BuildGuideUrl = RepoUrl + "#installation",
            ExecutableName = "ffsubsync",
            Tier = EngineTier.Supported,
            LinuxAmd64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/linux-x86_64.tar.gz",
                EnginePackaging.TarGz),
            LinuxArm64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/linux-arm64.tar.gz",
                EnginePackaging.TarGz),
            WindowsAmd64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/windows-x86_64.zip",
                EnginePackaging.Zip),
            MacAmd64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/macos-x86_64.tar.gz",
                EnginePackaging.TarGz),
            MacArm64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/macos-arm64.tar.gz",
                EnginePackaging.TarGz),
            Capabilities = new EngineCapabilities
            {
                SupportsStandard = true,
                SupportsAuto = false,
                SupportsOls = false,
                SupportsSplit = true,
                SupportsPenalty = true,
                DefaultPenalty = 7,
                MinPenalty = 0,
                MaxPenalty = 100,
                SubtitleExtensions = EngineFormats.Ffsubsync
            }
        };

        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Standard,
            "Single offset",
            "How ffsubsync ships. One offset for the whole file, plus a framerate correction if it spots one."));

        // ffsubsync's own docs call this "piecewise", but every other engine here calls the
        // same idea a split and that is what people search for, so it gets the common name.
        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Split,
            "Split",
            "Gives different parts of the file different timing. ffsubsync calls this piecewise mode."));

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "vad",
            Label = "Voice detector",
            Description = "Which speech detector to use on the audio. auditok often does better on noisy films; webrtc is the default and the fastest.",
            Flag = "--vad",
            Kind = EngineParameterKind.Select,
            DefaultValue = "webrtc"
        });
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("webrtc", "webrtc (default)"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("auditok", "auditok"));
        descriptor.Parameters[^1].Options.Add(new EngineParameterOption("silero", "silero"));

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "maxOffsetSeconds",
            Label = "Maximum offset (seconds)",
            Description = "How far ffsubsync is allowed to move the subtitle. Anything beyond this is rejected rather than applied.",
            Flag = "--max-offset-seconds",
            Kind = EngineParameterKind.Number,
            DefaultValue = "600",
            Minimum = 1
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "noFixFramerate",
            Label = "Do not correct framerate",
            Description = "Stops ffsubsync trying framerate ratios. Leave off unless it is picking the wrong one.",
            Flag = "--no-fix-framerate",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "gss",
            Label = "Golden section search",
            Description = "Searches for the framerate ratio instead of trying a fixed list. Slower, and better on unusual ratios.",
            Flag = "--gss",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "frameRate",
            Label = "Frame rate",
            Description = "The rate ffsubsync samples audio at while looking for speech. Its own default is 25.",
            Flag = "--frame-rate",
            Kind = EngineParameterKind.Number,
            DefaultValue = "25",
            Minimum = 1
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "maxSubtitleSeconds",
            Label = "Maximum cue length (seconds)",
            Description = "Cues longer than this are treated as junk rather than as speech. Default 55.",
            Flag = "--max-subtitle-seconds",
            Kind = EngineParameterKind.Number,
            DefaultValue = "55",
            Minimum = 1,
            Step = 0.5
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "startSeconds",
            Label = "Start at (seconds)",
            Description = "Skips the first part of the reference. Useful when a film opens with a long musical passage.",
            Flag = "--start-seconds",
            Kind = EngineParameterKind.Number,
            DefaultValue = "0",
            Minimum = 0
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "referenceStream",
            Label = "Reference stream",
            Description = "Which stream of the video to line up against, in ffmpeg's notation, e.g. a:0 or s:0. Blank lets ffsubsync pick.",
            Flag = "--reference-stream",
            Kind = EngineParameterKind.Text,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "encoding",
            Label = "Input encoding",
            Description = "Character encoding to read the subtitle with.",
            Flag = "--encoding",
            Kind = EngineParameterKind.Text,
            DefaultValue = "utf-8"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "outputEncoding",
            Label = "Output encoding",
            Description = "Character encoding to write the result with. \"same\" keeps whatever the input used.",
            Flag = "--output-encoding",
            Kind = EngineParameterKind.Text,
            DefaultValue = "utf-8"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "strict",
            Label = "Strict parsing",
            Description = "Refuses to read a malformed subtitle rather than doing its best with it.",
            Flag = "--strict",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        return descriptor;
    }

    private static void AddNumber(List<string> args, EngineParameterValues values, string key, string flag)
    {
        if (!values.ShouldPass(key))
        {
            return;
        }

        var value = values.GetNumber(key);
        if (value.HasValue)
        {
            args.Add(flag);
            args.Add(EngineParameterValues.Format(value.Value));
        }
    }

    private static void AddText(List<string> args, EngineParameterValues values, string key, string flag)
    {
        if (values.ShouldPass(key))
        {
            args.Add(flag);
            args.Add(values.GetString(key));
        }
    }
}
