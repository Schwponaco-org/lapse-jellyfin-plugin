// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// The LAPSE engine. Its CLI is:
/// lapse &lt;video_or_subtitle&gt; &lt;subtitle&gt; [ols|nosplit|split] [penalty]
/// and it rewrites the subtitle it was given, so we hand it a copy at the output path
/// and let it edit that.
/// </summary>
public partial class LapseEngine : IEngine
{
    // Done (nosplit): offset=1250ms -> /path/to/out.srt
    [GeneratedRegex(@"^Done \(nosplit\): offset=(?<offset>-?[0-9]+)ms -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex NosplitOutputRegex();

    // Done (ols): slope=1.0021 intercept=0.42s -> /path/to/out.srt
    [GeneratedRegex(@"^Done \(ols\): slope=(?<slope>[-0-9.eE+]+) intercept=(?<intercept>[-0-9.eE+]+)s -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex OlsOutputRegex();

    // Done (split, p=6): /path/to/out.srt
    [GeneratedRegex(@"^Done \(split, p=(?<penalty>[-0-9.eE+]+)\): (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex SplitOutputRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = new()
    {
        Id = "lapse",
        DisplayName = "LAPSE",
        Description = "Language agnostic subtitle sync. The only engine here that can do OLS alignment.",
        ProjectUrl = "https://github.com/rs-jensen/lapse",
        ExecutableName = "lapse",
        Packaging = EnginePackaging.RawBinary,
        EditsInPlace = true,
        Amd64Url = "https://github.com/rs-jensen/lapse/releases/latest/download/lapse-linux-amd64",
        Arm64Url = "https://github.com/rs-jensen/lapse/releases/latest/download/lapse-linux-arm64",
        Capabilities = new EngineCapabilities
        {
            SupportsStandard = true,
            SupportsOls = true,
            SupportsSplit = true,
            SupportsPenalty = true,
            DefaultPenalty = 6,
            MinPenalty = 0,
            MaxPenalty = 100
        }
    };

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(string referencePath, string inputPath, string outputPath, SyncMode mode, int penalty, string? ffmpegDirectory)
    {
        var modeArgument = mode switch
        {
            SyncMode.Ols => "ols",
            SyncMode.Split => "split",
            _ => "nosplit"
        };

        // LAPSE edits the subtitle it's handed rather than taking a separate output path,
        // so the caller copies the input to outputPath first and we point LAPSE at that.
        var args = new List<string> { referencePath, outputPath, modeArgument };

        if (mode == SyncMode.Split)
        {
            args.Add(penalty.ToString(CultureInfo.InvariantCulture));
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

        var nosplitMatch = NosplitOutputRegex().Match(stdout);
        if (nosplitMatch.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = SyncMode.Standard,
                OffsetMs = int.Parse(nosplitMatch.Groups["offset"].Value, CultureInfo.InvariantCulture)
            };
        }

        var olsMatch = OlsOutputRegex().Match(stdout);
        if (olsMatch.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = SyncMode.Ols,
                Slope = double.Parse(olsMatch.Groups["slope"].Value, CultureInfo.InvariantCulture),
                Intercept = double.Parse(olsMatch.Groups["intercept"].Value, CultureInfo.InvariantCulture)
            };
        }

        var splitMatch = SplitOutputRegex().Match(stdout);
        if (splitMatch.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = SyncMode.Split,
                Penalty = int.Parse(splitMatch.Groups["penalty"].Value, CultureInfo.InvariantCulture)
            };
        }

        return new SyncResult
        {
            Success = false,
            Mode = requestedMode,
            Error = "Engine finished but its output didn't look like a normal result. Check the server logs."
        };
    }
}
