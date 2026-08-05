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
    private readonly ILogger<EngineRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRunner"/> class.
    /// </summary>
    /// <param name="applicationPaths">Used to find where engines get installed.</param>
    /// <param name="registry">The known engines.</param>
    /// <param name="logger">Logger.</param>
    public EngineRunner(IApplicationPaths applicationPaths, EngineRegistry registry, ILogger<EngineRunner> logger)
    {
        _applicationPaths = applicationPaths;
        _registry = registry;
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

            var args = engine.BuildArguments(referencePath, subtitlePath, workPath, mode, penalty);
            var (stdout, stderr, exitCode) = await RunProcessAsync(enginePath, args, cancellationToken).ConfigureAwait(false);

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
