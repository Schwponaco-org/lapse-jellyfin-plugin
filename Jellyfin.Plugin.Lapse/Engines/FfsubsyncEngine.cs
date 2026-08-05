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
/// ffsubsync &lt;reference&gt; -i &lt;input&gt; -o &lt;output&gt;
/// It works out one offset for the whole file and can also correct a framerate mismatch.
/// It has no split alignment: its --multi-segment-sync option samples bits of the
/// reference to spot desync in long files, but still produces a single global result,
/// so this engine is standard only.
/// </summary>
public partial class FfsubsyncEngine : IEngine
{
    // ffsubsync logs a line with the offset it settled on. Same caveat as alass, this is
    // best effort and the run is really judged on exit code plus output file.
    [GeneratedRegex(@"offset\s*(?:seconds)?\s*[:=]\s*(?<seconds>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = new()
    {
        Id = "ffsubsync",
        DisplayName = "ffsubsync",
        Description = "Shifts the whole subtitle and can fix a framerate mismatch. No split alignment.",
        ProjectUrl = "https://github.com/smacke/ffsubsync",
        ExecutableName = "ffsubsync",
        Packaging = EnginePackaging.TarGz,
        Amd64Url = "https://github.com/smacke/ffsubsync/releases/latest/download/linux-x86_64.tar.gz",
        Arm64Url = "https://github.com/smacke/ffsubsync/releases/latest/download/linux-arm64.tar.gz",
        Capabilities = new EngineCapabilities
        {
            SupportsStandard = true,
            SupportsOls = false,
            SupportsSplit = false,
            SupportsPenalty = false,
            DefaultPenalty = 0,
            MinPenalty = 0,
            MaxPenalty = 0
        }
    };

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(string referencePath, string inputPath, string outputPath, SyncMode mode, int penalty)
    {
        return new List<string> { referencePath, "-i", inputPath, "-o", outputPath };
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
            Mode = SyncMode.Standard,

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
}
