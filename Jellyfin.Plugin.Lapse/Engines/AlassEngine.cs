// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

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
/// Flag names and defaults here come from the argument definitions in alass-cli's
/// main.rs, not from its README, which writes --no-split as "--no-splits" and would be
/// rejected by the binary. Splitting is on by default in alass with a penalty of 7.
/// </summary>
public partial class AlassEngine : IEngine
{
    private const string RepoUrl = "https://github.com/kaegi/alass";

    // alass prints its shift in seconds somewhere in its output, something along the lines
    // of "shift: 1.25s". We don't have a guaranteed format for this so it's best effort
    // only, and the run is judged on its exit code plus whether a file came out.
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

        AddNumber(args, values, "interval", "--interval");
        AddNumber(args, values, "speedOptimization", "--speed-optimization");
        AddNumber(args, values, "audioIndex", "--audio-index");
        AddNumber(args, values, "subFpsRef", "--sub-fps-ref");
        AddNumber(args, values, "subFpsInc", "--sub-fps-inc");
        AddText(args, values, "encodingRef", "--encoding-ref");
        AddText(args, values, "encodingInc", "--encoding-inc");

        if (values.GetBool("allowNegativeTimestamps"))
        {
            args.Add("--allow-negative-timestamps");
        }

        if (values.GetBool("disableFpsGuessing"))
        {
            args.Add("--disable-fps-guessing");
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
            EngineOutput = EngineResults.Summarize(stdout),
            Penalty = requestedMode == SyncMode.Split ? requestedPenalty : null
        };

        var match = ShiftRegex().Match(stdout);
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

            // alass only publishes x86_64 builds, so there's deliberately no arm64 or macOS
            // entry here. The installer turns that into a clear "no build for this machine"
            // message with a link to the build instructions.
            Capabilities = new EngineCapabilities
            {
                SupportsStandard = true,
                SupportsAuto = false,
                SupportsOls = false,
                SupportsSplit = true,
                SupportsPenalty = true,
                DefaultPenalty = 7,
                MinPenalty = 0,
                MaxPenalty = 1000
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

        // Blank, not "auto". alass's help prints "default: auto", but that describes what
        // it does when the flag is absent - the 2.0.0 binary hands the literal string to
        // encoding_rs and panics with "auto is not a known encoding label". Leave it off
        // and it detects the encoding itself, which is what everyone wants anyway.
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
