// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// alass by kaegi. Its CLI is:
///
/// alass &lt;reference&gt; &lt;incorrect&gt; &lt;output&gt;
///       [--split-penalty N] [--no-split] [--interval MS] [--speed-optimization N]
///       [--allow-negative-timestamps] [--disable-fps-guessing] [--audio-index N]
///       [--sub-fps-ref N] [--sub-fps-inc N] [--encoding-ref E] [--encoding-inc E]
///
/// Flags and defaults from alass-cli's main.rs, not the README (which says "--no-splits",
/// the binary rejects that). Splitting is on by default with penalty 7.
/// </summary>
public partial class AlassEngine : IEngine
{
    private const string RepoUrl = "https://github.com/kaegi/alass";

    // something like "shift: 1.25s", no guaranteed format so best effort. The run is
    // judged on exit code + whether a file came out.
    [GeneratedRegex(@"(?:shift|offset)[^-0-9]{0,12}(?<seconds>-?[0-9]+(?:\.[0-9]+)?)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex ShiftRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = BuildDescriptor();

    /// <inheritdoc />
    public bool NeedsSeededOutput(EngineRuntimeInfo runtime) => false;

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(EngineRunOptions options)
    {
        var values = options.Parameters;
        var args = new List<string> { options.ReferencePath, options.InputPath, options.OutputPath };

        if (options.Mode == SyncMode.Split)
        {
            args.Add("--split-penalty");
            args.Add(options.Penalty.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            // singular, per the argument definitions in alass's own source
            args.Add("--no-split");
        }

        values.AddNumber(args, "interval", "--interval");
        values.AddNumber(args, "speedOptimization", "--speed-optimization");
        values.AddNumber(args, "audioIndex", "--audio-index");
        values.AddNumber(args, "subFpsRef", "--sub-fps-ref");
        values.AddNumber(args, "subFpsInc", "--sub-fps-inc");
        values.AddText(args, "encodingRef", "--encoding-ref");
        values.AddText(args, "encodingInc", "--encoding-inc");

        values.AddSwitch(args, "allowNegativeTimestamps", "--allow-negative-timestamps");
        values.AddSwitch(args, "disableFpsGuessing", "--disable-fps-guessing");

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
            EngineOutput = EngineResults.Summarize(stdout),
            Penalty = requestedMode == SyncMode.Split ? requestedPenalty : null
        };

        var match = ShiftRegex().Match(stdout);
        if (match.Success && double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            result.OffsetMs = (int)Math.Round(seconds * 1000);
        }

        return result;
    }

    private static EngineDescriptor BuildDescriptor()
    {
        var descriptor = new EngineDescriptor
        {
            Id = "alass",
            DisplayName = "alass",
            Description = "Splits the subtitle into sections and times each one separately, which suits "
                + "recordings cut around ad breaks. Matches subtitles against subtitles or against the "
                + "video, and reports no confidence score.",
            ProjectUrl = RepoUrl,
            GitHubRepo = "kaegi/alass",
            BuildGuideUrl = RepoUrl + "#installation",
            ExecutableName = "alass",
            Tier = EngineTier.Supported,
            LinuxAmd64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/alass-linux64",
                EnginePackaging.RawBinary),
            WindowsAmd64 = new EngineDownload(
                RepoUrl + "/releases/latest/download/alass-windows64.zip",
                EnginePackaging.Zip),

            // alass only publishes x86_64 builds, the installer explains that for everything else
            Capabilities = new EngineCapabilities
            {
                SupportsStandard = true,
                SupportsAuto = false,
                SupportsOls = false,
                SupportsSplit = true,
                SupportsPenalty = true,
                DefaultPenalty = 7,
                MinPenalty = 0,
                MaxPenalty = 1000,
                SubtitleExtensions = EngineFormats.Alass
            }
        };

        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Split,
            "Split",
            "How alass ships. Looks for places the timing jumps and gives each section its own offset."));
        descriptor.Modes.Add(new EngineModeOption(
            SyncMode.Standard,
            "Single offset",
            "Passes --no-split, so one constant offset is applied to the whole file."));

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "interval",
            Label = "Interval (ms)",
            Description = "The smallest slice of time alass will work in. Bigger is faster and coarser.",
            Flag = "--interval",
            Kind = EngineParameterKind.Number,
            DefaultValue = "1",
            Minimum = 1
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "speedOptimization",
            Label = "Speed optimization",
            Description = "Trades a little accuracy for speed. 0 turns it off. alass defaults to 1.",
            Flag = "--speed-optimization",
            Kind = EngineParameterKind.Number,
            DefaultValue = "1",
            Minimum = 0
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "audioIndex",
            Label = "Audio track",
            Description = "Which audio stream of the reference video to use, counting from 0. Blank lets alass pick.",
            Flag = "--audio-index",
            Kind = EngineParameterKind.Number,
            Minimum = 0,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "disableFpsGuessing",
            Label = "Do not guess framerate differences",
            Description = "Stops alass correcting for a subtitle made at a different framerate. Leave off unless it is guessing wrong.",
            Flag = "--disable-fps-guessing",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "allowNegativeTimestamps",
            Label = "Allow negative timestamps",
            Description = "Lets cues end up before the start of the file instead of being clamped to zero.",
            Flag = "--allow-negative-timestamps",
            Kind = EngineParameterKind.Boolean,
            DefaultValue = "false"
        });

        // Blank, NOT "auto". The help says "default: auto" but alass 2.0.0 panics on the
        // literal string. Leaving it off autodetects anyway.
        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "encodingRef",
            Label = "Reference encoding",
            Description = "Leave blank and alass detects it. Only set this for a file it gets wrong, e.g. windows-1252.",
            Flag = "--encoding-ref",
            Kind = EngineParameterKind.Text,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "encodingInc",
            Label = "Input encoding",
            Description = "Same, for the subtitle being fixed.",
            Flag = "--encoding-inc",
            Kind = EngineParameterKind.Text,
            BlankMeansUnset = true
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "subFpsRef",
            Label = "Reference .sub framerate",
            Description = "Framerate to assume for a MicroDVD .sub reference, which stores frames rather than times.",
            Flag = "--sub-fps-ref",
            Kind = EngineParameterKind.Number,
            DefaultValue = "30",
            Minimum = 1,
            Step = 0.001
        });

        descriptor.Parameters.Add(new EngineParameter
        {
            Key = "subFpsInc",
            Label = "Input .sub framerate",
            Description = "Same, for a MicroDVD .sub file on the input side.",
            Flag = "--sub-fps-inc",
            Kind = EngineParameterKind.Number,
            DefaultValue = "30",
            Minimum = 1,
            Step = 0.001
        });

        return descriptor;
    }
}
