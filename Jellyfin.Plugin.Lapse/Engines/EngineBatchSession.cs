// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// One warm LAPSE process for a whole queue job, fed one sync at a time.
///
/// LAPSE 2.2.3+ has --batch: jobs come in on stdin one per line, answers go out as one
/// JSON line each. Loading onnxruntime and Silero is nothing for one Sync press but adds up
/// over a few thousand subtitles. Anything this can't carry (no --batch, not LAPSE, a path
/// with a quote or newline, a crashed process) falls back to one process per run.
/// </summary>
public sealed class EngineBatchSession : IDisposable
{
    // Answer on stdout, reason on stderr, read separately, so the reason can still be in
    // flight. A failed job waits for stderr to go quiet (up to the limit).
    private static readonly TimeSpan StderrQuiet = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan StderrLimit = TimeSpan.FromMilliseconds(750);

    // closing stdin makes it exit by itself, this is how long before we kill it
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private static readonly char[] Uncarriable = { '"', '\n', '\r' };

    // no BOM, it'd end up glued to the first job's first path
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private string? _binaryPath;
    private (DateTime WriteTimeUtc, long Length) _binaryStamp;
    private long _lastStderrTicks;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineBatchSession"/> class. Starts nothing until the first job.
    /// </summary>
    public EngineBatchSession(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how many jobs this session answered (across however many processes).
    /// </summary>
    public int JobsAnswered { get; private set; }

    /// <summary>
    /// Says whether these args fit on a job line. The engine splits on whitespace outside
    /// quotes with no escaping, so no quotes, newlines or empty args.
    /// </summary>
    public static bool CanCarry(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Length == 0 || arg.IndexOfAny(Uncarriable) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a job line, every arg quoted so spaces in paths just work.
    /// </summary>
    public static string BuildJobLine(IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();

        foreach (var arg in args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(arg).Append('"');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Runs one job, (re)starting the process if needed. Null means run it the normal way instead.
    /// </summary>
    public async Task<(string Stdout, string Stderr, int ExitCode)?> TryRunAsync(
        string binaryPath,
        IReadOnlyList<string> args,
        Action<ProcessStartInfo> prepare,
        CancellationToken cancellationToken)
    {
        if (!CanCarry(args))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var process = EnsureStarted(binaryPath, prepare);
            if (process is null)
            {
                return null;
            }

            lock (_stderrLock)
            {
                _stderr.Clear();
            }

            var jobLine = BuildJobLine(args);
            _logger.LogInformation("Running engine (batch): {Path} {Args}", binaryPath, jobLine);

            string? reply;
            try
            {
                await process.StandardInput.WriteAsync(jobLine + "\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                reply = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop();
                throw;
            }
            catch (IOException ex)
            {
                // broken pipe = process is gone
                _logger.LogWarning(ex, "LAPSE's batch process went away, running this job on its own instead");
                Stop();
                return null;
            }

            if (reply is null)
            {
                var said = await TakeStderrAsync(wait: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "LAPSE's batch process stopped part way through a job ({Said}), running that job on its own instead",
                    EngineResults.Summarize(said) ?? "it said nothing");
                Stop();
                return null;
            }

            JobsAnswered++;

            // a failed job just says {"error":true}, the why is on stderr so wait a moment
            // for it (it's what tells "damaged" from "too few cues")
            var failed = !reply.Contains("\"verdict\"", StringComparison.Ordinal);
            var stderr = await TakeStderrAsync(wait: failed).ConfigureAwait(false);

            return (failed ? string.Empty : reply, stderr, failed ? 1 : 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                process.StandardInput.Close();

                if (!process.WaitForExit(ExitGrace))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _logger.LogDebug(ex, "LAPSE's batch process did not close cleanly");
            }

            process.Dispose();
            _logger.LogInformation("Closed LAPSE's batch process after {Count} jobs", JobsAnswered);
        }

        _gate.Dispose();
    }

    private Process? EnsureStarted(string binaryPath, Action<ProcessStartInfo> prepare)
    {
        // size too, unpacked archives keep their packing time so two builds can share one
        var info = new FileInfo(binaryPath);
        var stamp = (info.LastWriteTimeUtc, info.Exists ? info.Length : -1);

        if (_process is not null)
        {
            if (!_process.HasExited
                && string.Equals(_binaryPath, binaryPath, StringComparison.Ordinal)
                && stamp == _binaryStamp)
            {
                return _process;
            }

            // gone, or updated underneath us (rename swap, the old process would keep the old build)
            Stop();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8
        };

        ProcessOutput.ReadAsUtf8(startInfo);
        startInfo.ArgumentList.Add("--batch");
        prepare(startInfo);

        var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += OnStderr;

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // the normal run after this reports it properly
            _logger.LogDebug(ex, "Could not start LAPSE in batch mode");
            process.Dispose();
            return null;
        }

        process.BeginErrorReadLine();

        _process = process;
        _binaryPath = binaryPath;
        _binaryStamp = stamp;

        _logger.LogInformation("Started LAPSE in batch mode, it stays loaded for the rest of this job");
        return process;
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        // late lines from a process we already stopped belong to an old job
        if (e.Data is null || !ReferenceEquals(sender, _process))
        {
            return;
        }

        lock (_stderrLock)
        {
            _stderr.AppendLine(e.Data);
            _lastStderrTicks = Environment.TickCount64;
        }
    }

    private async Task<string> TakeStderrAsync(bool wait)
    {
        if (wait)
        {
            var started = Environment.TickCount64;

            while (Environment.TickCount64 - started < StderrLimit.TotalMilliseconds)
            {
                long last;
                lock (_stderrLock)
                {
                    last = _lastStderrTicks;
                }

                if (Environment.TickCount64 - last >= StderrQuiet.TotalMilliseconds)
                {
                    break;
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            // quiet from the start can also mean it hasn't arrived yet, give it one more beat
            await Task.Delay(StderrQuiet).ConfigureAwait(false);
        }

        lock (_stderrLock)
        {
            var text = _stderr.ToString();
            _stderr.Clear();
            return text;
        }
    }

    private void Stop()
    {
        var process = _process;
        _process = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // tree, in case a build ever starts a helper
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not stop LAPSE's batch process");
        }

        process.Dispose();
    }
}
