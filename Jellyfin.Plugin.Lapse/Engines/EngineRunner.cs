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
    private readonly ILogger<EngineRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRunner"/> class.
    /// </summary>
    /// <param name="applicationPaths">Used to find where engines get installed.</param>
    /// <param name="registry">The known engines.</param>
    /// <param name="probe">Asks the installed binary which flags it understands.</param>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin already ships.</param>
    /// <param name="logger">Logger.</param>
    public EngineRunner(
        IApplicationPaths applicationPaths,
        EngineRegistry registry,
        EngineCapabilityProbe probe,
        IMediaEncoder mediaEncoder,
        ILogger<EngineRunner> logger)
    {
        _applicationPaths = applicationPaths;
        _registry = registry;
        _probe = probe;
        _mediaEncoder = mediaEncoder;
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
    /// Works out where a synced subtitle should end up under a given output mode. The
    /// sidecar modes put it next to the original with the configured suffix inserted, so
    /// Movie.en.srt becomes Movie.en.shifted.srt and Jellyfin picks it up as an extra
    /// track on its next scan.
    /// </summary>
    /// <param name="subtitlePath">The subtitle that's being synced.</param>
    /// <param name="mode">The output mode.</param>
    /// <returns>Where to write the result.</returns>
    public static string ResolveDestination(string subtitlePath, OutputMode mode)
    {
        if (mode is OutputMode.OverwriteWithBackup or OutputMode.OverwriteNoBackup)
        {
            return subtitlePath;
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

        return Path.Combine(directory, stem + suffix + extension);
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
        var resolvedOutputMode = ResolveOutputMode(outputMode);
        var destination = string.IsNullOrWhiteSpace(destinationOverride)
            ? ResolveDestination(subtitlePath, resolvedOutputMode)
            : destinationOverride;

        var workPath = subtitlePath + ".lapse-tmp" + Path.GetExtension(subtitlePath);

        try
        {
            if (engine.NeedsSeededOutput(runtime))
            {
                // this engine rewrites whatever file it's pointed at, so give it a copy to
                // chew on. For the others we leave the work path missing on purpose, so
                // "did anything get written" below is a real check rather than always true.
                File.Copy(subtitlePath, workPath, overwrite: true);
            }

            var ffmpegDirectory = GetFfmpegDirectory();
            var args = engine.BuildArguments(new EngineRunOptions
            {
                ReferencePath = referencePath,
                InputPath = subtitlePath,
                OutputPath = workPath,
                Mode = mode,
                Penalty = penalty,
                FfmpegDirectory = ffmpegDirectory,
                Runtime = runtime
            });

            var (stdout, stderr, exitCode) = await RunProcessAsync(enginePath, args, ffmpegDirectory, cancellationToken).ConfigureAwait(false);

            var result = engine.ParseResult(stdout, stderr, exitCode, mode, penalty);
            result.EngineId = engine.Descriptor.Id;

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

            var threshold = Plugin.Instance?.Configuration.SyncConfidenceThreshold ?? 50;
            result.LowConfidence = result.Confidence.HasValue && result.Confidence.Value * 100 < threshold;

            if (result.LowConfidence)
            {
                var action = Plugin.Instance?.Configuration.LowConfidenceAction ?? LowConfidenceAction.KeepOriginal;

                if (action == LowConfidenceAction.KeepOriginal)
                {
                    // A low score nearly always means the subtitle isn't for this video,
                    // and writing that over a subtitle that was already fine is the one
                    // outcome there's no undoing. The work file gets cleaned up in the
                    // finally block, so nothing on disk changes at all.
                    result.Skipped = true;
                    _logger.LogInformation(
                        "{Engine} came back at {Confidence:P0} on {Subtitle}, under the {Threshold}% threshold - leaving the original alone",
                        engine.Descriptor.DisplayName,
                        result.Confidence,
                        subtitlePath,
                        threshold);
                    return result;
                }

                if (action == LowConfidenceAction.Sidecar && string.IsNullOrWhiteSpace(destinationOverride))
                {
                    // Keep the original where it is and put the doubtful result beside it,
                    // whatever the output mode would normally have done.
                    destination = ResolveDestination(subtitlePath, OutputMode.SidecarOnly);
                    resolvedOutputMode = OutputMode.SidecarOnly;
                }
            }

            result.BackupPath = TakeBackup(destination, resolvedOutputMode);
            File.Move(workPath, destination, overwrite: true);
            result.OutputPath = destination;
            return result;
        }
        finally
        {
            CleanUp(workPath);
        }
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

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
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
