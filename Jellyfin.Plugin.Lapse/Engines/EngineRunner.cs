// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
/// working out the binary path, starting the process, and getting the output safely
/// onto disk in whichever shape the output mode asks for.
/// </summary>
public class EngineRunner
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly EngineRegistry _registry;
    private readonly EngineCapabilityProbe _probe;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly SubtitleConverter _converter;
    private readonly ILogger<EngineRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRunner"/> class.
    /// </summary>
    /// <param name="applicationPaths">Used to find where engines get installed.</param>
    /// <param name="registry">The known engines.</param>
    /// <param name="probe">Asks the installed binary which flags it understands.</param>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin already ships.</param>
    /// <param name="converter">Turns subtitles the engines can't read into ones they can.</param>
    /// <param name="logger">Logger.</param>
    public EngineRunner(
        IApplicationPaths applicationPaths,
        EngineRegistry registry,
        EngineCapabilityProbe probe,
        IMediaEncoder mediaEncoder,
        SubtitleConverter converter,
        ILogger<EngineRunner> logger)
    {
        _applicationPaths = applicationPaths;
        _registry = registry;
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
    /// Gets where an engine's binary lives once installed.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>Full path to the binary.</returns>
    public string GetInstalledPath(IEngine engine)
    {
        return Path.Combine(EnginesFolder, engine.Descriptor.Id, engine.Descriptor.GetExecutableFileName());
    }

    /// <summary>
    /// Works out which binary to run for an engine, honouring a configured path override.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>Full path to the binary to run.</returns>
    public string ResolvePath(IEngine engine)
    {
        var settings = Plugin.Instance?.Configuration.GetEngineSettings(engine.Descriptor.Id);
        var overridePath = settings?.PathOverride;

        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        return GetInstalledPath(engine);
    }

    /// <summary>
    /// Asks the installed binary what it supports.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What that binary can do.</returns>
    public Task<EngineRuntimeInfo> GetRuntimeInfoAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        return _probe.ProbeAsync(ResolvePath(engine), cancellationToken);
    }

    /// <summary>
    /// Gets whether there's a build of this engine for the machine the server runs on.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>True if the Install button can do anything.</returns>
    public static bool HasDownload(IEngine engine)
    {
        return engine.Descriptor.GetDownloadForThisMachine() is not null;
    }

    /// <summary>
    /// Gets the penalty to use for an engine, preferring what the caller asked for, then
    /// what's configured, then the engine's own default.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="requested">What the request asked for, if anything.</param>
    /// <returns>The penalty to pass on the command line.</returns>
    public static int ResolvePenalty(IEngine engine, int? requested)
    {
        if (requested.HasValue && requested.Value > 0)
        {
            return requested.Value;
        }

        var configured = Plugin.Instance?.Configuration.GetEngineSettings(engine.Descriptor.Id).Penalty;
        return configured ?? engine.Descriptor.Capabilities.DefaultPenalty;
    }

    /// <summary>
    /// Gets the mode a plain "Sync" press should use with an engine: what the admin picked
    /// in that engine's Advanced section, or the first mode the engine itself offers.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The mode to run.</returns>
    public static SyncMode ResolveDefaultMode(IEngine engine)
    {
        var configured = Plugin.Instance?.Configuration.GetEngineSettings(engine.Descriptor.Id).DefaultMode;

        if (!string.IsNullOrWhiteSpace(configured)
            && Enum.TryParse<SyncMode>(configured, ignoreCase: true, out var parsed)
            && engine.Descriptor.Modes.Exists(m => m.Mode == parsed))
        {
            return parsed;
        }

        return engine.Descriptor.Modes.Count > 0 ? engine.Descriptor.Modes[0].Mode : SyncMode.Standard;
    }

    /// <summary>
    /// Gets an engine's advanced parameters, merged with its own documented defaults.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The values to build a command line from.</returns>
    public static EngineParameterValues ResolveParameters(IEngine engine)
    {
        var saved = Plugin.Instance?.Configuration.GetEngineSettings(engine.Descriptor.Id).GetParameterMap();
        return new EngineParameterValues(engine.Descriptor.Parameters, saved);
    }

    /// <summary>
    /// Works out where a synced subtitle should end up under a given output mode. The
    /// sidecar modes put it next to the original with the configured suffix inserted, so
    /// Movie.en.srt becomes Movie.en.shifted.srt and Jellyfin picks it up as an extra
    /// track on its next scan.
    /// </summary>
    /// <param name="subtitlePath">The subtitle that's being synced.</param>
    /// <param name="mode">The output mode.</param>
    /// <param name="outputFormat">The format the result should be written in, without a
    /// dot, or null to keep the one it came in. Asking for a different format always
    /// leaves the original file alone, since the result can no longer take its name.</param>
    /// <returns>Where to write the result.</returns>
    public static string ResolveDestination(string subtitlePath, OutputMode mode, string? outputFormat = null)
    {
        if (mode is OutputMode.OverwriteWithBackup or OutputMode.OverwriteNoBackup)
        {
            return ApplyFormat(subtitlePath, outputFormat);
        }

        var suffix = Plugin.Instance?.Configuration.SidecarSuffix;
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = ".shifted";
        }

        if (!suffix.StartsWith('.'))
        {
            suffix = "." + suffix;
        }

        var directory = Path.GetDirectoryName(subtitlePath) ?? string.Empty;
        var extension = Path.GetExtension(subtitlePath);
        var stem = Path.GetFileNameWithoutExtension(subtitlePath);

        // syncing a file that's already a sidecar shouldn't stack another suffix onto it
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
        }

        return ApplyFormat(Path.Combine(directory, stem + suffix + extension), outputFormat);
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

    /// <summary>
    /// Gets the format conversions produce, falling back to srt when what's configured
    /// isn't a format the converter writes.
    /// </summary>
    /// <returns>The format name, without a dot.</returns>
    public static string ResolveConversionFormat()
    {
        var configured = Plugin.Instance?.Configuration.ConversionFormat;
        return SubtitleFormats.TryNormalizeOutputFormat(configured, out var format) ? format : "srt";
    }

    /// <summary>
    /// Gets the output mode a run should use, preferring what the caller asked for over
    /// what's configured.
    /// </summary>
    /// <param name="requested">What the request asked for, if anything.</param>
    /// <returns>The output mode.</returns>
    public static OutputMode ResolveOutputMode(OutputMode? requested)
    {
        return requested ?? Plugin.Instance?.Configuration.OutputMode ?? OutputMode.OverwriteWithBackup;
    }

    /// <summary>
    /// Checks whether an engine's binary can actually start, not just whether the file is
    /// there. A downloaded binary can still be broken, most commonly a missing shared
    /// library on whatever system it ended up on.
    /// </summary>
    /// <param name="engine">The engine to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null if it looks runnable, otherwise a short reason why not.</returns>
    public async Task<string?> CheckRunnableAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(engine);
        if (!File.Exists(path))
        {
            return "not installed yet";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // running with no arguments should fail fast either way (missing arguments, or a
        // loader error). Some engines are slow to start though, so give it a few seconds.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        return stderr.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase)
            ? stderr.Trim()
            : null;
    }

    /// <summary>
    /// Runs a sync. The engine always writes to a temporary file, which only gets moved
    /// into place once the run succeeded and actually produced something, so a failed or
    /// half finished run can't destroy a working subtitle.
    /// </summary>
    /// <param name="engine">Which engine to run.</param>
    /// <param name="referencePath">Video or reference subtitle to line up against.</param>
    /// <param name="subtitlePath">The subtitle to fix.</param>
    /// <param name="mode">Alignment mode.</param>
    /// <param name="penalty">Penalty for split mode.</param>
    /// <param name="outputMode">Where the result should land, or null for the configured default.</param>
    /// <param name="destinationOverride">An explicit file to write to, which wins over the
    /// output mode's own choice of destination. The mode still decides whether a file
    /// already sitting at that path gets backed up first.</param>
    /// <param name="outputFormat">The format to write the result in (srt, vtt, ass, ssa),
    /// or null to keep the format the subtitle came in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed result.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers validate paths before getting here: item syncs only accept a subtitle the library lists for that item, and subtitle to subtitle sync checks both paths are existing subtitle files.")]
    public async Task<SyncResult> RunAsync(
        IEngine engine,
        string referencePath,
        string subtitlePath,
        SyncMode mode,
        int penalty,
        OutputMode? outputMode = null,
        string? destinationOverride = null,
        string? outputFormat = null,
        CancellationToken cancellationToken = default)
    {
        var enginePath = ResolvePath(engine);
        if (!File.Exists(enginePath))
        {
            return new SyncResult
            {
                Success = false,
                Mode = mode,
                EngineId = engine.Descriptor.Id,
                Error = $"{engine.Descriptor.DisplayName} is not installed. Install it from the LAPSE dashboard first."
            };
        }

        var runtime = await _probe.ProbeAsync(enginePath, cancellationToken).ConfigureAwait(false);

        // Engines differ in what they read, so the question is asked of this engine rather
        // than assumed: LAPSE 2.0.3 takes eleven formats, including PGS and VobSub, and
        // writes each one back as itself, where alass and ffsubsync take four. Anything an
        // engine can't read gets converted first, because turning the item away for a
        // format the plugin can perfectly well read would be worse. On top of that, an
        // admin can ask for everything to be converted before syncing, which is the only
        // way to end up with one format across a library that has several.
        string? convertedInput = null;
        var enginePathIn = subtitlePath;

        var engineReads = runtime.CanRead(engine.Descriptor.Id, subtitlePath);
        var conversionFormat = ResolveConversionFormat();

        // Nothing to do when the file is already in the format that would be asked for.
        var convertAnyway = Plugin.Instance?.Configuration.ConvertBeforeSync == true
            && SubtitleFormats.IsTextBased(subtitlePath)
            && !string.Equals(SubtitleFormats.GetName(subtitlePath), conversionFormat, StringComparison.OrdinalIgnoreCase);

        if (!engineReads || convertAnyway)
        {
            if (_converter.GetConversionProblem(subtitlePath) is { } conversionProblem)
            {
                return new SyncResult
                {
                    Success = false,
                    Mode = mode,
                    EngineId = engine.Descriptor.Id,
                    Error = conversionProblem
                };
            }

            // A conversion the engine didn't ask for is the admin's choice of format; one
            // it did ask for goes to srt, which every engine reads.
            var convertTo = engineReads ? conversionFormat : "srt";

            convertedInput = subtitlePath + ".lapse-converted." + convertTo;

            try
            {
                await _converter.ConvertAsync(subtitlePath, convertedInput, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
            {
                CleanUp(convertedInput);
                return new SyncResult
                {
                    Success = false,
                    Mode = mode,
                    EngineId = engine.Descriptor.Id,
                    Error = "Could not convert that subtitle into something the engine can read: " + ex.Message
                };
            }

            enginePathIn = convertedInput;

            // The result has to come out in the converted format: there's no writing a
            // MicroDVD file back from srt cues, and quietly leaving the old file in place
            // would be worse than adding a new one beside it.
            outputFormat ??= convertTo;
        }

        var resolvedOutputMode = ResolveOutputMode(outputMode);
        var destination = string.IsNullOrWhiteSpace(destinationOverride)
            ? ResolveDestination(subtitlePath, resolvedOutputMode, outputFormat)
            : destinationOverride;

        var workPath = subtitlePath + ".lapse-tmp" + Path.GetExtension(enginePathIn);

        try
        {
            if (engine.NeedsSeededOutput(runtime))
            {
                // this engine rewrites whatever file it's pointed at, so give it a copy to
                // chew on. For the others we leave the work path missing on purpose, so
                // "did anything get written" below is a real check rather than always true.
                File.Copy(enginePathIn, workPath, overwrite: true);
            }

            var ffmpegDirectory = GetFfmpegDirectory();

            EngineRunOptions BuildOptions(bool forceAnyway) => new()
            {
                ReferencePath = referencePath,
                InputPath = enginePathIn,
                OutputPath = workPath,
                Mode = mode,
                Penalty = penalty,
                FfmpegDirectory = ffmpegDirectory,
                Runtime = runtime,
                Parameters = ResolveParameters(engine),
                ConfidenceSigma = Plugin.Instance?.Configuration.ConfidenceSigma ?? LapseEngine.DefaultConfidenceSigma,
                ForceAnyway = forceAnyway
            };

            var args = engine.BuildArguments(BuildOptions(false));

            var (stdout, stderr, exitCode) = await RunProcessAsync(enginePath, args, ffmpegDirectory, cancellationToken).ConfigureAwait(false);

            var result = engine.ParseResult(stdout, stderr, exitCode, mode, penalty);
            result.EngineId = engine.Descriptor.Id;

            // A short subtitle - forced signs, a few lines of foreign dialogue - has too
            // few cues for the engine to be sure of its answer, and it stops rather than
            // guessing. The file isn't wrong, there just isn't much of it, and the engine
            // says as much and points at its own --force. Someone pressed Sync on this,
            // so take it up rather than handing back a refusal they can do nothing about.
            if (!result.Success && IsTooFewCues(result.Error))
            {
                _logger.LogInformation(
                    "{Engine} says {Subtitle} is too short to judge, trying again with force",
                    engine.Descriptor.DisplayName,
                    subtitlePath);

                var forcedArgs = engine.BuildArguments(BuildOptions(true));

                if (!forcedArgs.SequenceEqual(args))
                {
                    (stdout, stderr, exitCode) = await RunProcessAsync(enginePath, forcedArgs, ffmpegDirectory, cancellationToken).ConfigureAwait(false);

                    result = engine.ParseResult(stdout, stderr, exitCode, mode, penalty);
                    result.EngineId = engine.Descriptor.Id;
                    result.Forced = result.Success;
                }
            }

            if (!result.Success)
            {
                _logger.LogWarning("{Engine} failed on {Subtitle}: {Error}", engine.Descriptor.DisplayName, subtitlePath, result.Error);

                // The message above is the summarized one the dashboard shows. Anyone
                // actually chasing a bad file needs the decoder chatter that was filtered
                // out of it, so it goes here rather than nowhere.
                _logger.LogDebug(
                    "{Engine} full output for {Subtitle}:\nstdout:\n{Stdout}\nstderr:\n{Stderr}",
                    engine.Descriptor.DisplayName,
                    subtitlePath,
                    stdout,
                    stderr);

                return result;
            }

            if (!File.Exists(workPath) || new FileInfo(workPath).Length == 0)
            {
                result.Success = false;
                result.Error = "The engine finished but didn't write anything out.";
                return result;
            }

            // The engine has already weighed its answer against the configured confidence
            // and said what it thinks of it, so gate on that rather than re-deriving a
            // judgement from a number here. Engines that report no verdict (alass,
            // ffsubsync) are never held to this and always get written.
            result.LowConfidence = result.Verdict is not null
                && !string.Equals(result.Verdict, "solid", StringComparison.OrdinalIgnoreCase);

            if (result.LowConfidence)
            {
                var action = Plugin.Instance?.Configuration.LowConfidenceAction ?? LowConfidenceAction.Sidecar;

                if (action == LowConfidenceAction.KeepOriginal)
                {
                    // A low score nearly always means the subtitle isn't for this video,
                    // and writing that over a subtitle that was already fine is the one
                    // outcome there's no undoing. The work file gets cleaned up in the
                    // finally block, so nothing on disk changes at all.
                    result.Skipped = true;
                    _logger.LogInformation(
                        "{Engine} came back {Verdict} on {Subtitle} (sigma {Sigma}, threshold {Threshold}) - leaving the original alone",
                        engine.Descriptor.DisplayName,
                        result.Verdict,
                        subtitlePath,
                        result.Sigma,
                        Plugin.Instance?.Configuration.ConfidenceSigma);
                    return result;
                }

                if (action == LowConfidenceAction.Sidecar && string.IsNullOrWhiteSpace(destinationOverride))
                {
                    // Keep the original where it is and put the doubtful result beside it,
                    // whatever the output mode would normally have done.
                    destination = ResolveDestination(subtitlePath, OutputMode.SidecarOnly, outputFormat);
                    resolvedOutputMode = OutputMode.SidecarOnly;
                }
            }

            // A subtitle that was already right is the common case in a bulk run, and the
            // engine reporting "move it 12ms" on one is not a reason to replace the file.
            // Only for a run that would otherwise have rewritten the subtitle in place:
            // an explicit destination or a format change means the caller asked for a new
            // file to exist, and that has to happen whatever the offset came out as.
            if (IsAlreadyInSync(result)
                && string.IsNullOrWhiteSpace(destinationOverride)
                && outputFormat is null)
            {
                result.Skipped = true;
                result.AlreadyInSync = true;

                _logger.LogInformation(
                    "{Engine} found {Subtitle} is already in sync (offset {Offset}ms, tolerance {Tolerance}ms) - leaving it alone",
                    engine.Descriptor.DisplayName,
                    subtitlePath,
                    result.OffsetMs,
                    Plugin.Instance?.Configuration.AlreadyInSyncToleranceMs);

                return result;
            }

            result.BackupPath = TakeBackup(destination, resolvedOutputMode);

            if (NeedsFormatChange(workPath, destination))
            {
                // The engine wrote in the format it was given; the caller asked for a
                // different one. Converting on the way out means one file lands, in the
                // format that was asked for, rather than a synced file plus a stray copy.
                await _converter.ConvertAsync(workPath, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Move(workPath, destination, overwrite: true);
            }

            CopyVobSubCompanion(subtitlePath, destination);

            result.OutputPath = destination;
            result.InputPath = subtitlePath;
            result.ConvertedFrom = convertedInput is null ? null : SubtitleFormats.GetName(subtitlePath);
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or TimeoutException or IOException)
        {
            // Only the format conversion on the way out throws these, and by here the
            // engine has already done its work, so this is a write failure rather than a
            // sync failure. Either way nothing landed.
            _logger.LogWarning(ex, "Could not write the synced subtitle as {Format}", outputFormat);

            return new SyncResult
            {
                Success = false,
                Mode = mode,
                EngineId = engine.Descriptor.Id,
                Error = "The sync worked, but writing the result out as " + outputFormat + " didn't: " + ex.Message
            };
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

    // A VobSub subtitle is two files: the .idx holds the timings, the .sub beside it holds
    // the pictures, and a player only finds the pictures by looking for a .sub named after
    // the .idx. Syncing one to a sidecar therefore writes a .idx that points at nothing,
    // so the pictures get copied over under the new name too. Overwriting in place needs
    // none of this, since the .sub it already had is still the right one.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Both paths are derived from a subtitle path the caller already validated.")]
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
            // The synced .idx is written either way; without its pictures it just won't
            // show anything, which is worth a line in the log rather than failing the run.
            _logger.LogWarning(ex, "Synced {Idx} but could not copy its .sub pictures to {Target}", subtitlePath, target);
        }
    }

    private static bool NeedsFormatChange(string workPath, string destination)
    {
        return !string.Equals(
            Path.GetExtension(workPath),
            Path.GetExtension(destination),
            StringComparison.OrdinalIgnoreCase);
    }

    // Older LAPSE builds always write a .bak next to the file they edit, and the file they
    // edit is our temporary work file, so that backup is junk. Newer builds take
    // --no-backup and never write it. Either way, nothing of ours should be left behind.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "This only ever deletes the temporary work file RunAsync just created from an already validated subtitle path.")]
    private static void CleanUp(string workPath)
    {
        foreach (var path in new[] { workPath, workPath + ".bak" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Copies whatever is at the destination to a .bak first, when the output mode asks
    /// for that. Shared with the manual shifter, which has to make the same promise about
    /// the file it's about to replace.
    /// </summary>
    /// <param name="destination">The file about to be written.</param>
    /// <param name="mode">The output mode in effect.</param>
    /// <returns>The backup path, or null if no backup was taken.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The destination comes from RunAsync, which derives it from a subtitle path the caller already validated.")]
    public static string? TakeBackup(string destination, OutputMode mode)
    {
        if (mode is not (OutputMode.OverwriteWithBackup or OutputMode.SidecarWithBackup))
        {
            return null;
        }

        // nothing to preserve the first time a sidecar gets written
        if (!File.Exists(destination))
        {
            return null;
        }

        var backupPath = destination + ".bak";
        File.Copy(destination, backupPath, overwrite: true);
        return backupPath;
    }

    /// <summary>
    /// Says whether the engine's answer amounts to "this was already fine". Deliberately
    /// strict about what it will call a no-op: the offset has to be one the engine
    /// actually reported (a null means it didn't say, which is not the same as zero), and
    /// anything that stretches the subtitle or cuts it into pieces gets written no matter
    /// how small the shift, because those change the timing across the file rather than
    /// nudging the whole thing.
    /// </summary>
    /// <param name="result">A successful engine result.</param>
    /// <returns>True if nothing worth writing came out of the run.</returns>
    private static bool IsAlreadyInSync(SyncResult result)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.SkipAlreadyInSync != true)
        {
            return false;
        }

        if (result.OffsetMs is not { } offset)
        {
            return false;
        }

        // A ratio correction moves later cues further than earlier ones, so a small
        // offset says nothing about what the run would do to the end of the file.
        if (result.Slope is not null || result.Mode == SyncMode.Split)
        {
            return false;
        }

        var tolerance = Math.Clamp(config.AlreadyInSyncToleranceMs, 0, 5000);
        return Math.Abs(offset) <= tolerance;
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        string enginePath,
        System.Collections.Generic.IReadOnlyList<string> args,
        string? ffmpegDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        AddFfmpegToPath(startInfo, ffmpegDirectory);

        _logger.LogInformation("Running engine: {Path} {Args}", enginePath, string.Join(' ', startInfo.ArgumentList));

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Waiting stops on cancellation, the engine doesn't. Without this a stopped
            // job leaves a decoder running over a whole film in the background.
            KillQuietly(process);
            throw;
        }

        return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
    }

    // The engine's own words for "there isn't enough of this file to work with", which it
    // ends with a pointer at --force. Matched on the text because that is all a failed
    // run gives us: there's no separate exit code for it.
    private static bool IsTooFewCues(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }

        return error.Contains("--force", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("not enough", StringComparison.OrdinalIgnoreCase)
                || error.Contains("too few", StringComparison.OrdinalIgnoreCase)
                || error.Contains("cues", StringComparison.OrdinalIgnoreCase));
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The engines start ffmpeg of their own, so the whole tree goes.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not stop the engine process after the job was cancelled");
        }
    }

    /// <summary>
    /// Gets the folder engines should use to find ffmpeg and ffprobe.
    ///
    /// On Linux this hands back a folder of tiny wrapper scripts rather than Jellyfin's
    /// real ffmpeg folder, because of how ffsubsync is packaged. It's a PyInstaller
    /// bundle, and PyInstaller points LD_LIBRARY_PATH at its own unpacked copy of
    /// everything. Any process it starts inherits that, so ffmpeg and ffprobe end up
    /// trying to load PyInstaller's libraries instead of their own and fall over with an
    /// unhelpful "ffprobe error". The wrappers clear that variable and then run the real
    /// binary. Windows and macOS don't have the problem, so they get the real folder.
    /// </summary>
    /// <returns>The folder to point engines at, or null if ffmpeg couldn't be found.</returns>
    public string? GetFfmpegDirectory()
    {
        var encoderPath = FirstExisting(_mediaEncoder.EncoderPath);
        var probePath = FirstExisting(_mediaEncoder.ProbePath);

        // ffprobe normally sits next to ffmpeg, so fall back to looking there for it
        if (probePath is null && encoderPath is not null)
        {
            var probeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            var sibling = Path.Combine(Path.GetDirectoryName(encoderPath)!, probeName);
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
        catch (IOException ex)
        {
            // couldn't write the wrappers, so fall back to the real folder. Engines that
            // don't care about LD_LIBRARY_PATH will be fine either way.
            _logger.LogWarning(ex, "Could not create the ffmpeg wrapper scripts, using {Folder} directly", realFolder);
            return realFolder;
        }
    }

    private static string? FirstExisting(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static void WriteShim(string shimFolder, string name, string? realPath)
    {
        // only ever called from the Linux branch above, but the analyzer can't follow that
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

        File.WriteAllText(shimPath, script);
        File.SetUnixFileMode(
            shimPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    // ffsubsync shells out to ffmpeg and ffprobe by name, and the Jellyfin docker images
    // don't have either on PATH - they live in the jellyfin-ffmpeg folder instead. Without
    // this ffsubsync dies with "No such file or directory: 'ffmpeg'". Jellyfin already
    // knows where its own build is, so borrow that rather than making people install
    // a second copy of ffmpeg. Engines that take an explicit ffmpeg path get told as well,
    // this is just so anything shelling out by name still works.
    private static void AddFfmpegToPath(ProcessStartInfo startInfo, string? ffmpegDirectory)
    {
        if (string.IsNullOrEmpty(ffmpegDirectory))
        {
            return;
        }

        var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = ffmpegDirectory + Path.PathSeparator + existing;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited on its own between the timeout firing and us getting here
        }
    }
}
