// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
/// onto disk.
/// </summary>
public class EngineRunner
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly EngineRegistry _registry;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<EngineRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRunner"/> class.
    /// </summary>
    /// <param name="applicationPaths">Used to find where engines get installed.</param>
    /// <param name="registry">The known engines.</param>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin already ships.</param>
    /// <param name="logger">Logger.</param>
    public EngineRunner(
        IApplicationPaths applicationPaths,
        EngineRegistry registry,
        IMediaEncoder mediaEncoder,
        ILogger<EngineRunner> logger)
    {
        _applicationPaths = applicationPaths;
        _registry = registry;
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
        return Path.Combine(EnginesFolder, engine.Descriptor.Id, engine.Descriptor.ExecutableName);
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
    /// Gets the URL to download an engine from for this machine, or null if the project
    /// doesn't publish a build for this architecture.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The download URL, or null.</returns>
    public static string? GetDownloadUrl(IEngine engine)
    {
        return RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? engine.Descriptor.Arm64Url
            : engine.Descriptor.Amd64Url;
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
    /// Runs a sync. Writes to a temp file next to the target and only moves it over the
    /// original once the engine succeeded and actually produced something, so a failed or
    /// half finished run can't destroy a working subtitle.
    /// </summary>
    /// <param name="engine">Which engine to run.</param>
    /// <param name="referencePath">Video or reference subtitle to line up against.</param>
    /// <param name="subtitlePath">The subtitle to fix, edited in place on success.</param>
    /// <param name="mode">Alignment mode.</param>
    /// <param name="penalty">Penalty for split mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed result.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers validate paths before getting here: movie syncs only accept a subtitle the library lists for that item, and subtitle to subtitle sync checks both paths are existing subtitle files.")]
    public async Task<SyncResult> RunAsync(
        IEngine engine,
        string referencePath,
        string subtitlePath,
        SyncMode mode,
        int penalty,
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

        var workPath = subtitlePath + ".lapse-tmp" + Path.GetExtension(subtitlePath);

        try
        {
            if (engine.Descriptor.EditsInPlace)
            {
                // this engine rewrites whatever file it's pointed at, so give it a copy to
                // chew on. For the others we leave the work path missing on purpose, so
                // "did anything get written" below is a real check rather than always true.
                File.Copy(subtitlePath, workPath, overwrite: true);
            }

            var ffmpegDirectory = GetFfmpegDirectory();
            var args = engine.BuildArguments(referencePath, subtitlePath, workPath, mode, penalty, ffmpegDirectory);
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

            File.Move(workPath, subtitlePath, overwrite: true);
            result.OutputPath = subtitlePath;
            return result;
        }
        finally
        {
            if (File.Exists(workPath))
            {
                File.Delete(workPath);
            }
        }
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
    /// This hands back a folder of tiny wrapper scripts rather than Jellyfin's real ffmpeg
    /// folder, because of how ffsubsync is packaged. It's a PyInstaller bundle, and
    /// PyInstaller points LD_LIBRARY_PATH at its own unpacked copy of everything. Any
    /// process it starts inherits that, so ffmpeg and ffprobe end up trying to load
    /// PyInstaller's libraries instead of their own and fall over with an unhelpful
    /// "ffprobe error". The wrappers clear that variable and then run the real binary.
    /// </summary>
    /// <returns>The folder to point engines at, or null if ffmpeg couldn't be found.</returns>
    public string? GetFfmpegDirectory()
    {
        var encoderPath = FirstExisting(_mediaEncoder.EncoderPath);
        var probePath = FirstExisting(_mediaEncoder.ProbePath);

        // ffprobe normally sits next to ffmpeg, so fall back to looking there for it
        if (probePath is null && encoderPath is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(encoderPath)!, "ffprobe");
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
    // Engines that look for ffmpeg on PATH rather than taking an explicit option get the
    // same wrapper folder, so they behave the same way.
    private void AddFfmpegToPath(ProcessStartInfo startInfo, string? ffmpegDirectory)
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
