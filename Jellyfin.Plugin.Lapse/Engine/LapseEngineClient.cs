// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engine;

/// <summary>
/// Runs the LAPSE engine binary and reads back what it printed.
/// </summary>
public partial class LapseEngineClient
{
    // Matches: Done (OLS): slope=1.0021 intercept=0.42s -> /path/to/out.srt
    [GeneratedRegex(@"^Done \(OLS\): slope=(?<slope>[-0-9.eE+]+) intercept=(?<intercept>[-0-9.eE+]+)s -> (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex OlsOutputRegex();

    // Matches: Done (split, p=6): /path/to/out.srt
    [GeneratedRegex(@"^Done \(split, p=(?<penalty>[-0-9.eE+]+)\): (?<path>.+)$", RegexOptions.Multiline)]
    private static partial Regex SplitOutputRegex();

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<LapseEngineClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseEngineClient"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths, used to find the default engine folder.</param>
    /// <param name="logger">Logger.</param>
    public LapseEngineClient(IApplicationPaths applicationPaths, ILogger<LapseEngineClient> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Gets the folder the engine binary gets downloaded into.
    /// </summary>
    public string EngineFolder => Path.Combine(_applicationPaths.DataPath, "lapse", "engines");

    /// <summary>
    /// Gets the default engine binary path (before considering any path override).
    /// </summary>
    public string DefaultEnginePath => Path.Combine(EngineFolder, "lapse");

    /// <summary>
    /// Works out which engine binary to actually run, taking the configured override into account.
    /// </summary>
    /// <returns>Full path to the binary to run.</returns>
    public string ResolveEnginePath()
    {
        var overridePath = Plugin.Instance?.Configuration.EngineBinaryPathOverride;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        return DefaultEnginePath;
    }

    /// <summary>
    /// Checks whether the engine binary can actually start, not just whether the file exists.
    /// A downloaded binary can still be broken - most commonly a missing shared library on
    /// whatever system it ends up running on (this bit us during testing: the binary needs
    /// libavcodec/libavformat/libavutil/libfvad/libfftw3, none of which the Jellyfin docker
    /// image ships).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null if the engine looks runnable, otherwise a short description of why not.</returns>
    public async Task<string?> CheckRunnableAsync(CancellationToken cancellationToken = default)
    {
        var enginePath = ResolveEnginePath();
        if (!File.Exists(enginePath))
        {
            return "not downloaded yet";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
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

        // running the binary with no arguments should fail fast either way (missing
        // arguments, or a loader error) - if it's still going after a couple seconds
        // something else is happening, but that's not a "can't start" problem
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
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

    /// <summary>
    /// Syncs a subtitle against a movie's video (or another subtitle) file.
    /// </summary>
    /// <param name="videoOrSubtitlePath">Path to the video (or reference subtitle) file.</param>
    /// <param name="subtitlePath">Path to the subtitle that needs to be lined up.</param>
    /// <param name="mode">Standard (OLS) or split alignment.</param>
    /// <param name="penalty">Penalty value, only used for split mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed result.</returns>
    public async Task<SyncResult> RunAsync(
        string videoOrSubtitlePath,
        string subtitlePath,
        SyncMode mode,
        int penalty,
        CancellationToken cancellationToken = default)
    {
        var args = new[] { videoOrSubtitlePath, subtitlePath };
        var penaltyArg = mode == SyncMode.Split ? penalty : (int?)null;

        var (stdout, stderr, exitCode) = await RunProcessAsync(args, penaltyArg, cancellationToken).ConfigureAwait(false);

        return ParseOutput(stdout, stderr, exitCode, mode);
    }

    /// <summary>
    /// Syncs one subtitle file against another. Always standard (OLS) mode, no penalty.
    /// </summary>
    /// <param name="referencePath">The subtitle that's already correctly timed.</param>
    /// <param name="inputPath">The subtitle that needs to be lined up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed result.</returns>
    public async Task<SyncResult> RunSubtitleToSubtitleAsync(
        string referencePath,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var args = new[] { referencePath, inputPath };
        var (stdout, stderr, exitCode) = await RunProcessAsync(args, null, cancellationToken).ConfigureAwait(false);

        return ParseOutput(stdout, stderr, exitCode, SyncMode.Ols);
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        string[] args,
        int? penalty,
        CancellationToken cancellationToken)
    {
        var enginePath = ResolveEnginePath();

        if (!File.Exists(enginePath))
        {
            _logger.LogWarning("LAPSE engine binary not found at {Path}", enginePath);
            return (string.Empty, $"Engine binary not found at {enginePath}. Download it from the LAPSE dashboard page first.", -1);
        }

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

        if (penalty.HasValue)
        {
            startInfo.ArgumentList.Add(penalty.Value.ToString(CultureInfo.InvariantCulture));
        }

        _logger.LogInformation("Running LAPSE engine: {Path} {Args}", enginePath, string.Join(' ', startInfo.ArgumentList));

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

    private SyncResult ParseOutput(string stdout, string stderr, int exitCode, SyncMode requestedMode)
    {
        if (exitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(stderr) ? $"Engine exited with code {exitCode}" : stderr.Trim();
            _logger.LogWarning("LAPSE engine failed: {Error}", error);
            return new SyncResult { Success = false, Mode = requestedMode, Error = error };
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
                OutputPath = olsMatch.Groups["path"].Value.Trim()
            };
        }

        var splitMatch = SplitOutputRegex().Match(stdout);
        if (splitMatch.Success)
        {
            return new SyncResult
            {
                Success = true,
                Mode = SyncMode.Split,
                Penalty = int.Parse(splitMatch.Groups["penalty"].Value, CultureInfo.InvariantCulture),
                OutputPath = splitMatch.Groups["path"].Value.Trim()
            };
        }

        _logger.LogWarning("Could not parse LAPSE engine output: {Output}", stdout);
        return new SyncResult
        {
            Success = false,
            Mode = requestedMode,
            Error = "Engine finished but its output didn't look like a normal result. Check the server logs."
        };
    }
}
