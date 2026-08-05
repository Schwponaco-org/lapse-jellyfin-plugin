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
/// alass &lt;reference&gt; &lt;incorrect&gt; &lt;output&gt; [--split-penalty N] [--no-splits]
/// Note the penalty scale is nothing like LAPSE's: it runs 0 to 1000 and defaults to 7.
/// </summary>
public partial class AlassEngine : IEngine
{
    // alass prints its shift in seconds somewhere in its output, something along the lines
    // of "shift: 1.25s". We don't have a guaranteed format for this so it's best effort
    // only, and the run is judged on its exit code plus whether a file came out.
    [GeneratedRegex(@"(?:shift|offset)[^-0-9]{0,12}(?<seconds>-?[0-9]+(?:\.[0-9]+)?)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex ShiftRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = new()
    {
        Id = "alass",
        DisplayName = "alass",
        Description = "Handles subtitles that drift unevenly, for example around ad breaks. No OLS mode.",
        ProjectUrl = "https://github.com/kaegi/alass",
        ExecutableName = "alass",
        Packaging = EnginePackaging.RawBinary,
        Amd64Url = "https://github.com/kaegi/alass/releases/latest/download/alass-linux64",

        // alass only publishes an x86_64 build, so there's deliberately no arm64 URL here.
        // The installer turns that into a clear "no build for this architecture" message.
        Arm64Url = null,
        Capabilities = new EngineCapabilities
        {
            SupportsStandard = true,
            SupportsOls = false,
            SupportsSplit = true,
            SupportsPenalty = true,
            DefaultPenalty = 7,
            MinPenalty = 0,
            MaxPenalty = 1000
        }
    };

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(string referencePath, string inputPath, string outputPath, SyncMode mode, int penalty)
    {
        var args = new List<string> { referencePath, inputPath, outputPath };

        if (mode == SyncMode.Split)
        {
            args.Add("--split-penalty");
            args.Add(penalty.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            // singular. alass's own README writes this as --no-splits, which the binary
            // rejects, so this comes from the argument definitions in its source instead.
            args.Add("--no-split");
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
}
