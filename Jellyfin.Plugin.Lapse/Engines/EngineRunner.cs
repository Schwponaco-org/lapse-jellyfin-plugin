// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Runs whichever engine it's handed. Everything that isn't engine specific lives here:
/// finding the binary, starting the process and getting the output onto disk the way the
/// output mode wants it.
/// </summary>
public class EngineRunner
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly EngineCapabilityProbe _probe;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly SubtitleConverter _converter;
    private readonly ILogger<EngineRunner> _logger;

    // last answer from GetFfmpegDirectory and the two paths it was worked out from
    private (string? Encoder, string? Probe, string? Folder)? _ffmpegFolder;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRunner"/> class.
    /// </summary>
    public EngineRunner(
        IApplicationPaths applicationPaths,
        EngineCapabilityProbe probe,
        IMediaEncoder mediaEncoder,
        SubtitleConverter converter,
        ILogger<EngineRunner> logger)
    {
        _applicationPaths = applicationPaths;
        _probe = probe;
        _mediaEncoder = mediaEncoder;
        _converter = converter;
        _logger = logger;
    }

    /// <summary>
    /// Gets the folder engines get installed into.
    /// </summary>
    public string EnginesFolder => Path.Combine(_applicationPaths.DataPath, "lapse", "engines");

    /// <summary>
    /// Gets where an engine's binary lives once we've installed it.
    /// </summary>
    public string GetInstalledPath(IEngine engine)
    {
        return Path.Combine(EnginesFolder, engine.Descriptor.Id, engine.Descriptor.GetExecutableFileName());
    }

    /// <summary>
    /// Gets the binary to run for an engine, path override first.
    /// </summary>
    public string ResolvePath(IEngine engine)
    {
        var overridePath = Plugin.Instance?.Configuration.FindEngineSettings(engine.Descriptor.Id)?.PathOverride;
        return !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath) ? overridePath : GetInstalledPath(engine);
    }

    /// <summary>
    /// Asks the binary what it supports.
    /// </summary>
    public Task<EngineRuntimeInfo> GetRuntimeInfoAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        return _probe.ProbeAsync(ResolvePath(engine), cancellationToken);
    }

    /// <summary>
    /// Starts a batch session for a job that's about to sync lots of files. Nothing runs
    /// until the first file, and engines that can't do batch just run each file on its own.
    /// </summary>
    public EngineBatchSession CreateBatchSession()
    {
        return new EngineBatchSession(_logger);
    }

    /// <summary>
    /// Asks LAPSE which voice detector it's using ("silero", or the weaker "libfvad"
    /// fallback). Other engines don't have this so they get null without starting anything.
    /// </summary>
    public Task<string?> GetVadBackendAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        return IsLapse(engine)
            ? _probe.ProbeVadBackendAsync(ResolvePath(engine), cancellationToken)
            : Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Checks the binary actually starts, not just that it's there. Downloaded builds can
    /// still be missing a shared library on whatever box they landed on.
    /// </summary>
    public Task<string?> CheckRunnableAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        return _probe.CheckRunnableAsync(ResolvePath(engine), cancellationToken);
    }

    /// <summary>
    /// Gets whether there's a build of this engine for this machine.
    /// </summary>
    public static bool HasDownload(IEngine engine)
    {
        return engine.Descriptor.GetDownloadForThisMachine() is not null;
    }

    /// <summary>
    /// Gets the penalty to use: what was asked for, then what's configured, then the engine default.
    /// </summary>
    public static int ResolvePenalty(IEngine engine, int? requested)
    {
        if (requested is > 0)
        {
            return requested.Value;
        }

        return Plugin.Instance?.Configuration.FindEngineSettings(engine.Descriptor.Id)?.Penalty
            ?? engine.Descriptor.Capabilities.DefaultPenalty;
    }

    /// <summary>
    /// Gets the mode a plain Sync press uses: the admin's pick, or the engine's first mode.
    /// </summary>
    public static SyncMode ResolveDefaultMode(IEngine engine)
    {
        var configured = Plugin.Instance?.Configuration.FindEngineSettings(engine.Descriptor.Id)?.DefaultMode;

        if (Enum.TryParse<SyncMode>(configured, ignoreCase: true, out var parsed)
            && engine.Descriptor.Modes.Exists(m => m.Mode == parsed))
        {
            return parsed;
        }

        return engine.Descriptor.Modes.Count > 0 ? engine.Descriptor.Modes[0].Mode : SyncMode.Standard;
    }

    /// <summary>
    /// Gets an engine's advanced parameters merged with its defaults.
    /// </summary>
    public static EngineParameterValues ResolveParameters(IEngine engine)
    {
        Dictionary<string, string?>? saved;

        // the settings page replaces this list on save, so copy it under the same lock
        lock (Plugin.ConfigurationLock)
        {
            saved = Plugin.Instance?.Configuration.FindEngineSettings(engine.Descriptor.Id)?.GetParameterMap();
        }

        return new EngineParameterValues(engine.Descriptor.Parameters, saved);
    }

    /// <summary>
    /// Works out where a synced subtitle goes. Sidecar modes put it next to the original
    /// with the suffix added (Movie.en.srt becomes Movie.en.shifted.srt) so Jellyfin picks
    /// it up as an extra track.
    /// </summary>
    public static string ResolveDestination(string subtitlePath, OutputMode mode, string? outputFormat = null)
    {
        if (mode is OutputMode.OverwriteWithBackup or OutputMode.OverwriteNoBackup)
        {
            return ApplyFormat(subtitlePath, outputFormat);
        }

        var suffix = ResolveSidecarSuffix();
        var directory = Path.GetDirectoryName(subtitlePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(subtitlePath);

        // don't stack a second suffix on something that's already a sidecar
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
        }

        return ApplyFormat(Path.Combine(directory, stem + suffix + Path.GetExtension(subtitlePath)), outputFormat);
    }

    /// <summary>
    /// Says whether a subtitle is the sidecar an earlier sync wrote for another one in the
    /// list (Movie.en.shifted.srt next to Movie.en.srt). Unattended runs skip those, or
    /// they'd do the same work twice and a doubtful answer would become its own input.
    /// </summary>
    public static bool IsSidecarOfAnother(string path, IEnumerable<string> all)
    {
        var suffix = ResolveSidecarSuffix();
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || stem.Length == suffix.Length)
        {
            return false;
        }

        var originalStem = stem[..^suffix.Length];
        var folder = Path.GetDirectoryName(path);

        // compare without extension, Movie.en.sub can come out as Movie.en.shifted.srt
        return all.Any(other =>
            string.Equals(Path.GetDirectoryName(other), folder, StringComparison.Ordinal)
            && string.Equals(Path.GetFileNameWithoutExtension(other), originalStem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the conversion format, srt if what's configured isn't one we can write.
    /// </summary>
    public static string ResolveConversionFormat()
    {
        return SubtitleFormats.TryNormalizeOutputFormat(Plugin.Instance?.Configuration.ConversionFormat, out var format) ? format : "srt";
    }

    /// <summary>
    /// Gets the format a subtitle gets converted to before the engine sees it, or null when it
    /// goes in as it is. A converted subtitle comes back out in that format too, so callers
    /// that pick the result's file name themselves need to know.
    /// </summary>
    public static string? ResolveConversionTarget(EngineRuntimeInfo runtime, IEngine engine, string subtitlePath)
    {
        // Ask the engine what it reads instead of assuming (LAPSE does fifteen formats incl.
        // PGS/VobSub, alass and ffsubsync four). Anything it can't read gets converted to srt,
        // everything reads that. Admins can also ask for everything to be converted, to end up
        // with one format.
        var engineReads = runtime.CanRead(engine.Descriptor.Id, subtitlePath);
        var conversionFormat = ResolveConversionFormat();

        var convertAnyway = Plugin.Instance?.Configuration.ConvertBeforeSync == true
            && SubtitleFormats.IsTextBased(subtitlePath)
            && !string.Equals(SubtitleFormats.GetName(subtitlePath), conversionFormat, StringComparison.OrdinalIgnoreCase);

        return !engineReads ? "srt" : convertAnyway ? conversionFormat : null;
    }

    /// <summary>
    /// Gets the output mode: what was asked for, else what's configured.
    /// </summary>
    public static OutputMode ResolveOutputMode(OutputMode? requested)
    {
        return requested ?? Plugin.Instance?.Configuration.OutputMode ?? OutputMode.SidecarOnly;
    }

    /// <summary>
    /// Gets the output mode for one subtitle. A track we extracted from the video gets written
    /// over instead of getting a sidecar, the original is still inside the video and a sidecar
    /// would have Jellyfin list the same track three times.
    /// </summary>
    public static OutputMode ResolveOutputMode(OutputMode? requested, string? videoPath, string subtitlePath)
    {
        var mode = ResolveOutputMode(requested);

        if (!ExtractedSubtitleNames.IsExtractedTrack(videoPath, subtitlePath))
        {
            return mode;
        }

        return mode switch
        {
            OutputMode.SidecarOnly => OutputMode.OverwriteNoBackup,
            OutputMode.SidecarWithBackup => OutputMode.OverwriteWithBackup,
            _ => mode
        };
    }

    /// <summary>
    /// Says whether a subtitle was extracted from the video it's being synced against:
    /// same folder, starts with the video's name and has the ".stream8" (or older ".track8")
    /// marker an extraction adds, possibly with a sidecar suffix after it.
    /// </summary>
    public static bool IsOwnTrack(string referencePath, string subtitlePath)
    {
        if (SubtitleFormats.IsSubtitle(referencePath)
            || !string.Equals(Path.GetDirectoryName(referencePath), Path.GetDirectoryName(subtitlePath), StringComparison.Ordinal))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(referencePath);
        var name = Path.GetFileNameWithoutExtension(subtitlePath);

        if (!name.StartsWith(stem + ".", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var token in name[(stem.Length + 1)..].Split('.'))
        {
            var digits = token.StartsWith("stream", StringComparison.Ordinal) ? token[6..]
                : token.StartsWith("track", StringComparison.Ordinal) ? token[5..]
                : null;

            if (!string.IsNullOrEmpty(digits) && digits.All(char.IsAsciiDigit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies the file at the destination to .bak first, if the output mode wants that.
    /// The manual shifter uses this too.
    /// </summary>
    public static string? TakeBackup(string destination, OutputMode mode)
    {
        // nothing to keep the first time a sidecar is written
        if (mode is not (OutputMode.OverwriteWithBackup or OutputMode.SidecarWithBackup) || !File.Exists(destination))
        {
            return null;
        }

        var backupPath = destination + ".bak";
        File.Copy(destination, backupPath, overwrite: true);
        return backupPath;
    }

    /// <summary>
    /// Runs a sync. The engine always writes to a temp file that only gets moved into place
    /// once the run worked and actually wrote something, so a failed run can't wreck a good subtitle.
    /// </summary>
    public async Task<SyncResult> RunAsync(
        IEngine engine,
        string referencePath,
        string subtitlePath,
        SyncMode mode,
        int penalty,
        OutputMode? outputMode = null,
        string? destinationOverride = null,
        string? outputFormat = null,
        bool skipConfidencePolicy = false,
        EngineBatchSession? batch = null,
        string? fromVideoPath = null,
        CancellationToken cancellationToken = default)
    {
        // runs on the same subtitle share a work file, so they take turns
        using var fileLock = await SubtitleFileLock.AcquireAsync(subtitlePath, cancellationToken).ConfigureAwait(false);

        var enginePath = ResolvePath(engine);
        if (!File.Exists(enginePath))
        {
            return Failed(engine, mode, $"{engine.Descriptor.DisplayName} is not installed. Install it from the LAPSE dashboard first.");
        }

        var runtime = await _probe.ProbeAsync(enginePath, cancellationToken).ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration;

        // the other engines have nothing like it, and an older LAPSE would sync against the
        // speech and quietly write a subtitle for the wrong copy
        if (fromVideoPath is not null && (!IsLapse(engine) || !runtime.HasFlag("--from")))
        {
            return Failed(engine, mode, "Moving a subtitle from another copy needs LAPSE 2.3.0 or newer. Update the engine from the LAPSE dashboard first.");
        }

        string? convertedInput = null;
        var engineInput = subtitlePath;

        if (ResolveConversionTarget(runtime, engine, subtitlePath) is { } convertTo)
        {
            if (_converter.GetConversionProblem(subtitlePath) is { } conversionProblem)
            {
                return Failed(engine, mode, conversionProblem);
            }

            convertedInput = subtitlePath + ".lapse-converted." + convertTo;

            try
            {
                await _converter.ConvertAsync(subtitlePath, convertedInput, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
            {
                CleanUp(convertedInput);
                return Failed(engine, mode, "Could not convert that subtitle into something the engine can read: " + ex.Message);
            }

            engineInput = convertedInput;

            // the result has to come out converted too, we can't write MicroDVD back from srt cues
            outputFormat ??= convertTo;
        }

        var resolvedOutputMode = ResolveOutputMode(outputMode, referencePath, subtitlePath);
        var destination = string.IsNullOrWhiteSpace(destinationOverride)
            ? ResolveDestination(subtitlePath, resolvedOutputMode, outputFormat)
            : destinationOverride;

        var workPath = subtitlePath + ".lapse-tmp" + Path.GetExtension(engineInput);

        try
        {
            // Engines that rewrite the file they're pointed at get a copy. The others get no
            // file on purpose, so "did it write anything" below means something.
            if (engine.NeedsSeededOutput(runtime))
            {
                File.Copy(engineInput, workPath, overwrite: true);
            }

            var ffmpegDirectory = GetFfmpegDirectory();
            var parameters = ResolveParameters(engine);
            var environment = engine.GetEnvironment(parameters);
            var ignoreEmbedded = IsOwnTrack(referencePath, subtitlePath);

            EngineRunOptions BuildOptions(bool forceAnyway) => new()
            {
                ReferencePath = referencePath,
                InputPath = engineInput,
                OutputPath = workPath,
                Mode = mode,
                Penalty = penalty,
                FfmpegDirectory = ffmpegDirectory,
                Runtime = runtime,
                Parameters = parameters,
                ConfidenceSigma = config?.ConfidenceSigma ?? LapseEngine.DefaultConfidenceSigma,
                ForceAnyway = forceAnyway,
                IgnoreEmbedded = ignoreEmbedded,
                FromVideoPath = fromVideoPath
            };

            // warm batch process if we have one and this build supports it, otherwise (or if
            // the batch process can't take this job) a process of its own
            var useBatch = batch is not null && IsLapse(engine) && runtime.SupportsBatch;

            async Task<SyncResult> Run(IReadOnlyList<string> arguments)
            {
                var output = useBatch
                    ? await batch!.TryRunAsync(enginePath, arguments, si => PrepareEnvironment(si, ffmpegDirectory, environment), cancellationToken).ConfigureAwait(false)
                    : null;

                if (output is null)
                {
                    _logger.LogInformation("Running engine: {Path} {Args}", enginePath, EngineBatchSession.BuildJobLine(arguments));

                    // No time limit, a long film just takes a while and the Stop button is the
                    // way out. Cancelling kills the engine and the ffmpeg it started.
                    var process = await ProcessRunner
                        .RunAsync(enginePath, arguments, null, cancellationToken, si => PrepareEnvironment(si, ffmpegDirectory, environment))
                        .ConfigureAwait(false);

                    output = (process.Stdout, process.Stderr, process.ExitCode);
                }

                var (stdout, stderr, exitCode) = output.Value;
                var parsed = engine.ParseResult(stdout, stderr, exitCode, mode, penalty);
                parsed.EngineId = engine.Descriptor.Id;

                if (!parsed.Success)
                {
                    // the dashboard gets the short version, the full chatter goes to the debug log
                    _logger.LogDebug(
                        "{Engine} full output for {Subtitle}:\nstdout:\n{Stdout}\nstderr:\n{Stderr}",
                        engine.Descriptor.DisplayName,
                        subtitlePath,
                        stdout,
                        stderr);
                }

                return parsed;
            }

            var args = engine.BuildArguments(BuildOptions(false));
            var result = await Run(args).ConfigureAwait(false);

            // A short subtitle (forced signs, a few lines) has too few cues for the engine to be
            // sure and it says so, pointing at --force. Someone pressed Sync, so try that.
            if (!result.Success && IsTooFewCues(result.Error))
            {
                _logger.LogInformation("{Engine} says {Subtitle} is too short to judge, trying again with force", engine.Descriptor.DisplayName, subtitlePath);

                var forcedArgs = engine.BuildArguments(BuildOptions(true));
                if (!forcedArgs.SequenceEqual(args))
                {
                    result = await Run(forcedArgs).ConfigureAwait(false);
                    result.Forced = result.Success;
                }
            }

            if (!result.Success)
            {
                _logger.LogWarning("{Engine} failed on {Subtitle}: {Error}", engine.Descriptor.DisplayName, subtitlePath, result.Error);
                return result;
            }

            if (!File.Exists(workPath) || new FileInfo(workPath).Length == 0)
            {
                result.Success = false;
                result.Error = "The engine finished but didn't write anything out.";
                return result;
            }

            // Go by the engine's own verdict, it already weighed the answer against the
            // configured confidence. Engines without a verdict (alass, ffsubsync) always get
            // written. A forced retry is always doubtful: with a handful of cues --force
            // calls anything solid, and in testing that wrote a result 11 seconds out.
            result.LowConfidence = result.Forced
                || (result.Verdict is not null && !string.Equals(result.Verdict, "solid", StringComparison.OrdinalIgnoreCase));

            // Candidate runs skip this, they were asked for because LAPSE was unsure and
            // the whole point is to get the answer on disk to compare.
            if (result.LowConfidence && !skipConfidencePolicy)
            {
                var action = config?.LowConfidenceAction ?? LowConfidenceAction.Sidecar;

                if (action == LowConfidenceAction.KeepOriginal)
                {
                    // a low score usually means the subtitle isn't for this video, and writing
                    // that over a good one can't be undone. finally cleans up the work file.
                    result.Skipped = true;
                    _logger.LogInformation(
                        "{Engine} came back {Verdict} on {Subtitle} (sigma {Sigma}, threshold {Threshold}), leaving the original alone",
                        engine.Descriptor.DisplayName,
                        result.Verdict,
                        subtitlePath,
                        result.Sigma,
                        config?.ConfidenceSigma);
                    return result;
                }

                if (action == LowConfidenceAction.Sidecar)
                {
                    // Put the doubtful answer next to the original whatever the output mode says.
                    // A caller that picked the file itself still gets it there, as long as that
                    // doesn't mean writing over something.
                    if (string.IsNullOrWhiteSpace(destinationOverride))
                    {
                        destination = ResolveDestination(subtitlePath, OutputMode.SidecarOnly, outputFormat);
                        resolvedOutputMode = OutputMode.SidecarOnly;
                    }
                    else if (File.Exists(destination))
                    {
                        destination = ResolveDestination(destination, OutputMode.SidecarOnly, outputFormat);
                        resolvedOutputMode = OutputMode.SidecarOnly;
                    }

                    result.Unconfirmed = true;
                }
            }

            // "Move it 12ms" isn't a reason to rewrite a file. Only applies when we'd rewrite
            // in place, an explicit destination or format change means a new file was wanted.
            if (IsAlreadyInSync(result) && string.IsNullOrWhiteSpace(destinationOverride) && outputFormat is null)
            {
                result.Skipped = true;

                // near zero only means "already right" when the engine trusts its answer
                if (result.LowConfidence && !skipConfidencePolicy && config?.LowConfidenceAction != LowConfidenceAction.OverwriteAnyway)
                {
                    result.Unconfirmed = false;
                    _logger.LogInformation(
                        "{Engine} came back {Verdict} on {Subtitle} with an offset of {Offset}ms, too doubtful to call it in sync and too small to be worth a file",
                        engine.Descriptor.DisplayName,
                        result.Verdict,
                        subtitlePath,
                        result.OffsetMs);
                    return result;
                }

                result.AlreadyInSync = true;
                _logger.LogInformation(
                    "{Engine} found {Subtitle} is already in sync (offset {Offset}ms, tolerance {Tolerance}ms), leaving it alone",
                    engine.Descriptor.DisplayName,
                    subtitlePath,
                    result.OffsetMs,
                    config?.AlreadyInSyncToleranceMs);
                return result;
            }

            result.BackupPath = TakeBackup(destination, resolvedOutputMode);

            // the engine writes in the format it was given, convert on the way out if a
            // different one was asked for so only one file lands
            if (!string.Equals(Path.GetExtension(workPath), Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
            {
                await _converter.ConvertAsync(workPath, destination, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Move(workPath, destination, overwrite: true);
            }

            CopyVobSubCompanion(subtitlePath, destination);

            result.OutputPath = destination;
            result.InputPath = subtitlePath;
            result.ReplacedExtractedCopy = string.Equals(destination, subtitlePath, StringComparison.Ordinal)
                && ExtractedSubtitleNames.IsExtractedTrack(referencePath, subtitlePath);
            result.ConvertedFrom = convertedInput is null ? null : SubtitleFormats.GetName(subtitlePath);
            return result;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // binary's there but won't run: no exec bit, wrong arch, noexec mount
            _logger.LogWarning(ex, "Could not start {Engine} at {Path}", engine.Descriptor.DisplayName, enginePath);
            return Failed(engine, mode, $"{engine.Descriptor.DisplayName} could not be started: {ex.Message}");
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            // backup, move or convert-on-the-way-out failed. The engine did its job, the write didn't happen.
            _logger.LogWarning(ex, "Could not write the synced subtitle for {Subtitle}", subtitlePath);
            return Failed(
                engine,
                mode,
                outputFormat is null
                    ? "The sync worked, but writing the result didn't: " + ex.Message
                    : "The sync worked, but writing the result out as " + outputFormat + " didn't: " + ex.Message);
        }
        finally
        {
            CleanUp(workPath);

            if (convertedInput is not null)
            {
                CleanUp(convertedInput);
            }
        }
    }

    /// <summary>
    /// Gets the folder engines should find ffmpeg and ffprobe in.
    ///
    /// On Linux that's a folder of tiny wrapper scripts rather than Jellyfin's real one.
    /// ffsubsync is a PyInstaller bundle that sets LD_LIBRARY_PATH to its own libs, and
    /// ffmpeg inherits it and falls over. The wrappers unset it first. Windows and macOS
    /// don't have that problem.
    /// </summary>
    public string? GetFfmpegDirectory()
    {
        var encoderPath = FirstExisting(_mediaEncoder.EncoderPath);
        var probePath = FirstExisting(_mediaEncoder.ProbePath);

        if (_ffmpegFolder is { } cached && cached.Encoder == encoderPath && cached.Probe == probePath)
        {
            return cached.Folder;
        }

        var folder = WorkOutFfmpegDirectory(encoderPath, probePath);
        _ffmpegFolder = (encoderPath, probePath, folder);
        return folder;
    }

    private static bool IsLapse(IEngine engine)
    {
        return string.Equals(engine.Descriptor.Id, "lapse", StringComparison.OrdinalIgnoreCase);
    }

    private static SyncResult Failed(IEngine engine, SyncMode mode, string error)
    {
        return new SyncResult { Success = false, Mode = mode, EngineId = engine.Descriptor.Id, Error = error };
    }

    private static string ResolveSidecarSuffix()
    {
        return Plugin.Instance?.Configuration.ResolveSidecarSuffix() ?? ".shifted";
    }

    private static string ApplyFormat(string path, string? outputFormat)
    {
        if (string.IsNullOrEmpty(outputFormat)
            || string.Equals(Path.GetExtension(path).TrimStart('.'), outputFormat, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.ChangeExtension(path, "." + outputFormat);
    }

    // VobSub is two files: .idx has the timings, .sub the pictures, and players find the
    // .sub by the .idx's name. A sidecar .idx needs its own copy of the pictures.
    private void CopyVobSubCompanion(string subtitlePath, string destination)
    {
        if (!string.Equals(Path.GetExtension(subtitlePath), ".idx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subtitlePath, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var source = Path.ChangeExtension(subtitlePath, ".sub");
        var target = Path.ChangeExtension(destination, ".sub");

        if (!File.Exists(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the .idx is written anyway, it just won't show anything. worth a log line, not a failed run
            _logger.LogWarning(ex, "Synced {Idx} but could not copy its .sub pictures to {Target}", subtitlePath, target);
        }
    }

    // Removes the work file and the .bak older LAPSE builds always write next to whatever
    // they edit (newer ones take --no-backup).
    private void CleanUp(string workPath)
    {
        foreach (var path in new[] { workPath, workPath + ".bak" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // don't throw from a finally, it'd replace the real result
                _logger.LogDebug(ex, "Could not remove the work file {Path}", path);
            }
        }
    }

    // Strict on purpose. The offset has to be one the engine reported (null isn't zero),
    // and a stretch or split changes timing across the whole file however small the shift.
    // So does --snap, it moves cues onto cuts even when the offset itself is zero.
    private static bool IsAlreadyInSync(SyncResult result)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.SkipAlreadyInSync != true || result.OffsetMs is not { } offset)
        {
            return false;
        }

        if (result.Slope is not null || result.Snapped > 0)
        {
            return false;
        }

        var tolerance = Math.Clamp(config.AlreadyInSyncToleranceMs, 0, 5000);

        // A split only counts when LAPSE said where every part went (2.3.0) and none of them
        // moved further than a whole file would be allowed to.
        if (result.Mode == SyncMode.Split)
        {
            return result.PartOffsetsMs is { Count: > 0 } parts && parts.All(part => Math.Abs(part) <= tolerance);
        }

        return Math.Abs(offset) <= tolerance;
    }

    // The engine's own "not enough cues, use --force" message. Only the text tells us, there's
    // no separate exit code for it.
    private static bool IsTooFewCues(string? error)
    {
        return !string.IsNullOrEmpty(error)
            && error.Contains("--force", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("not enough", StringComparison.OrdinalIgnoreCase)
                || error.Contains("too few", StringComparison.OrdinalIgnoreCase)
                || error.Contains("cues", StringComparison.OrdinalIgnoreCase));
    }

    private string? WorkOutFfmpegDirectory(string? encoderPath, string? probePath)
    {
        // ffprobe normally sits next to ffmpeg
        if (probePath is null && encoderPath is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(encoderPath)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            probePath = File.Exists(sibling) ? sibling : null;
        }

        if (encoderPath is null && probePath is null)
        {
            return null;
        }

        var realFolder = Path.GetDirectoryName(encoderPath ?? probePath!)!;
        if (!OperatingSystem.IsLinux())
        {
            return realFolder;
        }

        try
        {
            var shimFolder = Path.Combine(_applicationPaths.DataPath, "lapse", "ffmpeg-shim");
            Directory.CreateDirectory(shimFolder);
            WriteShim(shimFolder, "ffmpeg", encoderPath);
            WriteShim(shimFolder, "ffprobe", probePath);
            return shimFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // engines that don't care about LD_LIBRARY_PATH are fine with the real folder
            _logger.LogWarning(ex, "Could not create the ffmpeg wrapper scripts, using {Folder} directly", realFolder);
            return realFolder;
        }
    }

    private static string? FirstExisting(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    // Leaves a wrapper alone if it already says the right thing, and swaps in a new one by
    // rename so an engine that's halfway through running the old one isn't affected.
    private static void WriteShim(string shimFolder, string name, string? realPath)
    {
        // only called on Linux, the check is for the analyzer
        if (realPath is null || !OperatingSystem.IsLinux())
        {
            return;
        }

        var shimPath = Path.Combine(shimFolder, name);
        var script = "#!/bin/sh\n"
            + "# Written by the LAPSE Jellyfin plugin.\n"
            + "# PyInstaller based engines hand their children an LD_LIBRARY_PATH pointing at\n"
            + "# their own bundled libraries, which stops ffmpeg loading the ones it needs.\n"
            + "unset LD_LIBRARY_PATH\n"
            + $"exec \"{realPath}\" \"$@\"\n";

        if (File.Exists(shimPath) && string.Equals(File.ReadAllText(shimPath), script, StringComparison.Ordinal))
        {
            return;
        }

        var staged = shimPath + ".lapse-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(staged, script);
        File.SetUnixFileMode(
            staged,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(staged, shimPath, overwrite: true);
    }

    private static void PrepareEnvironment(ProcessStartInfo startInfo, string? ffmpegDirectory, IReadOnlyDictionary<string, string> environment)
    {
        // ffsubsync is Python and writes to pipes in the locale encoding, we read UTF-8
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        if (string.IsNullOrEmpty(ffmpegDirectory))
        {
            return;
        }

        // The Jellyfin docker images don't have ffmpeg on PATH (it's in the jellyfin-ffmpeg
        // folder), and ffsubsync calls it by name. Borrow Jellyfin's instead of making people
        // install a second copy.
        startInfo.Environment["PATH"] = ffmpegDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        // alass checks these before PATH, and its Windows zip expects them set to the ffmpeg
        // it ships. We only install the binary, so point it at Jellyfin's.
        SetIfPresent(startInfo, "ALASS_FFMPEG_PATH", ffmpegDirectory, "ffmpeg");
        SetIfPresent(startInfo, "ALASS_FFPROBE_PATH", ffmpegDirectory, "ffprobe");
    }

    private static void SetIfPresent(ProcessStartInfo startInfo, string variable, string folder, string toolName)
    {
        var path = Path.Combine(folder, OperatingSystem.IsWindows() ? toolName + ".exe" : toolName);
        if (File.Exists(path))
        {
            startInfo.Environment[variable] = path;
        }
    }
}
