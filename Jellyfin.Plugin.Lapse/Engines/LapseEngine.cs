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
///       [--output &lt;path&gt;] [--no-backup] [--no-embedded]
/// where the flags only exist on newer builds. On a build without --output it rewrites
/// the subtitle it was given, so we hand it a copy at the output path instead and let it
/// edit that. --no-embedded turns off preferring a subtitle track already inside the
/// reference video over decoding its audio, which the plugin never needs to set: an
/// already-correct embedded track is exactly what "reference" is supposed to mean here.
/// </summary>
public partial class LapseEngine : IEngine
{
    private static EngineDownload BuildLinuxDownload(string arch)
    {
        var download = new EngineDownload(
            $"https://github.com/rs-jensen/lapse/releases/latest/download/lapse-linux-{arch}",
            EnginePackaging.RawBinary);

        // The release asset is named libonnxruntime-linux-{arch}.so, but the engine's own
        // loader (silero.cpp, load_runtime()) only ever looks beside itself for the bare
        // "libonnxruntime.so" - it doesn't know the arch-suffixed name. Save it under the
        // name the engine actually searches for, not the name GitHub gave it.
        download.Sidecars.Add(new EngineSidecarAsset(
            $"https://github.com/rs-jensen/lapse/releases/latest/download/libonnxruntime-linux-{arch}.so",
            "libonnxruntime.so"));
        download.Sidecars.Add(new EngineSidecarAsset(
            "https://github.com/rs-jensen/lapse/releases/latest/download/silero_vad.onnx",
            "silero_vad.onnx"));

        return download;
    }

    // Done (nosplit): offset=1250ms -> /path/to/out.srt
    // Newer builds add a confidence to the same line, so everything after the numbers we
    // care about is deliberately loose:
    // Done (nosplit): offset=1250ms confidence=0.82 -> /path/to/out.srt
    [GeneratedRegex(@"^Done \(nosplit\): offset=(?<offset>-?[0-9]+)ms(?<extra>.*?) -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex NosplitOutputRegex();

    // Done (ols): slope=1.0021 intercept=0.42s [confidence=0.9] -> /path/to/out.srt
    [GeneratedRegex(@"^Done \(ols\): slope=(?<slope>[-0-9.eE+]+) intercept=(?<intercept>[-0-9.eE+]+)s(?<extra>.*?) -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex OlsOutputRegex();

    // Done (split, p=6): /path/to/out.srt
    // Newer builds print "Done (split, p=6): base=120ms confidence=0.7 -> /path/to/out.srt"
    [GeneratedRegex(@"^Done \(split, p=(?<penalty>[-0-9.eE+]+)\):(?<extra>.*)$", RegexOptions.Multiline)]
    private static partial Regex SplitOutputRegex();

    // confidence=0.82, wherever it turns up on the line
    [GeneratedRegex(@"confidence=(?<confidence>[0-9.eE+-]+)")]
    private static partial Regex ConfidenceRegex();

    /// <inheritdoc />
    public EngineDescriptor Descriptor { get; } = new()
    {
        Id = "lapse",
        DisplayName = "LAPSE",
        Description = "Language agnostic subtitle sync. The only engine here that can do OLS alignment.",
        ProjectUrl = "https://github.com/rs-jensen/lapse",
        GitHubRepo = "rs-jensen/lapse",
        BuildGuideUrl = "https://github.com/rs-jensen/lapse#cli",
        ExecutableName = "lapse",
        EditsInPlace = true,
        Experimental = true,

        // Upstream only publishes Linux builds. There's deliberately no Windows or macOS
        // entry here, which is what makes the dashboard show the build-it-yourself help
        // instead of handing out a download that would not run.
        //
        // The Silero VAD path (better voice detection than the built-in libfvad) needs
        // onnxruntime and its model sitting next to the binary. Both are listed as sidecars
        // rather than baked into what "install" requires, because the engine itself falls
        // back to libfvad when either is missing - which is also what happens on any
        // release that predates these two assets existing, so an install stays a success
        // either way, just with the older VAD until a release actually carries them.
        LinuxAmd64 = BuildLinuxDownload("amd64"),
        LinuxArm64 = BuildLinuxDownload("arm64"),
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
    public bool NeedsSeededOutput(EngineRuntimeInfo runtime)
    {
        // With --output it writes wherever we point it. Without, it edits in place and the
        // only way to get the result somewhere else is to give it a copy to work on.
        return !runtime.SupportsOutputFlag;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(EngineRunOptions options)
    {
        var modeArgument = options.Mode switch
        {
            SyncMode.Ols => "ols",
            SyncMode.Split => "split",
            _ => "nosplit"
        };

        var supportsOutput = options.Runtime.SupportsOutputFlag;

        // Without --output, LAPSE rewrites the file it's handed, so the caller copies the
        // input to the output path first and we point LAPSE at that copy.
        var subtitleArgument = supportsOutput ? options.InputPath : options.OutputPath;
        var args = new List<string> { options.ReferencePath, subtitleArgument, modeArgument };

        if (options.Mode == SyncMode.Split)
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
        if (options.Runtime.SupportsNoBackupFlag)
        {
            args.Add("--no-backup");
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
                OffsetMs = int.Parse(nosplitMatch.Groups["offset"].Value, CultureInfo.InvariantCulture),
                Confidence = ReadConfidence(nosplitMatch.Value)
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
                Intercept = double.Parse(olsMatch.Groups["intercept"].Value, CultureInfo.InvariantCulture),
                Confidence = ReadConfidence(olsMatch.Value)
            };
        }

        var splitMatch = SplitOutputRegex().Match(stdout);
        if (splitMatch.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = SyncMode.Split,
                Penalty = (int)double.Parse(splitMatch.Groups["penalty"].Value, CultureInfo.InvariantCulture),
                Confidence = ReadConfidence(splitMatch.Value)
            };
        }

        return new SyncResult
        {
            Success = false,
            Mode = requestedMode,
            Error = "Engine finished but its output didn't look like a normal result. Check the server logs."
        };
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
