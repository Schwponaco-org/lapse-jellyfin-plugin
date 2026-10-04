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
/// One LAPSE process kept running for a whole queue job, fed one sync at a time.
///
/// LAPSE 2.2.3 added --batch: rather than syncing the file named on its command line, it
/// reads jobs from stdin, one line each with the same arguments, and answers every line
/// with one line of JSON. Starting the binary loads onnxruntime and the Silero model,
/// which is nothing for one press of Sync and real time across a library of a few
/// thousand subtitles, so the unattended runs keep one process warm instead of starting
/// one per file.
///
/// Anything this can't carry goes back to the ordinary one process per run: a build
/// without --batch, an engine other than LAPSE, a path the job line has no way to hold (it
/// has no escapes, so a double quote or a line break can't be passed), or a process that
/// died part way through a job. The caller gets the same answer either way.
/// </summary>
public sealed class EngineBatchSession : IDisposable
{
    // The answer comes down stdout and the reason for a failure down stderr, two pipes
    // read separately, so the answer can arrive while the last of the reason is still in
    // flight. A failed job waits until stderr has been quiet this long, within the limit.
    private static readonly TimeSpan StderrQuiet = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan StderrLimit = TimeSpan.FromMilliseconds(750);

    // Closing stdin ends the engine's read loop and it exits on its own. This is how long
    // it gets to do that before it's killed.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private static readonly char[] Uncarriable = { '"', '\n', '\r' };

    // No BOM: a byte order mark in front of the first job would become part of its first
    // path, and that file doesn't exist.
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
    /// Initializes a new instance of the <see cref="EngineBatchSession"/> class. Nothing
    /// is started until the first job arrives.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public EngineBatchSession(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how many jobs this session has answered, across however many processes it
    /// took.
    /// </summary>
    public int JobsAnswered { get; private set; }

    /// <summary>
    /// Says whether a set of arguments can go down a job line. The engine splits a line on
    /// whitespace outside double quotes and drops the quotes, with no way to escape one, so
    /// a quote in a path would cut it in two. An empty argument would vanish altogether.
    /// </summary>
    /// <param name="args">The arguments a one-shot run would get.</param>
    /// <returns>True if the line would reach the engine intact.</returns>
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
    /// Builds the line one job is sent as. Every argument goes in quotes, which carries
    /// spaces and tabs in paths without having to think about which ones have them.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The job line, without its line break.</returns>
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
    /// Runs one job through the warm process, starting it first if it isn't running or the
    /// binary on disk has changed since it was started.
    /// </summary>
    /// <param name="binaryPath">The LAPSE binary.</param>
    /// <param name="args">The arguments a one-shot run would get.</param>
    /// <param name="prepare">Sets up the environment, same as for a one-shot run.</param>
    /// <param name="cancellationToken">Cancellation token. Cancelling stops the process,
    /// the same as it stops a one-shot run, and the next job starts a fresh one.</param>
    /// <returns>What the job printed, shaped like a one-shot run's output with an exit code
    /// of 1 for a job the engine couldn't do, or null when this job has to be run on its
    /// own instead.</returns>
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
                // The pipe broke, which means the process is gone.
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

            // {"error":true} is the whole of what the engine sends back for a job it
            // couldn't do. Why is on stderr, which is worth waiting a moment for: it's
            // what tells a damaged file from one with too few cues to judge.
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path is the plugin's own engine binary or an admin-set override, the same one a one-shot run starts.")]
    private Process? EnsureStarted(string binaryPath, Action<ProcessStartInfo> prepare)
    {
        // Size as well as time: an archive unpacks with the times it was packed with, so
        // two builds can share one.
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

            // Gone, or the engine was updated underneath it. The installer swaps files by
            // renaming, so the old process would carry on happily running the old build
            // for the rest of a long job; starting again picks up the new one.
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
            // The one-shot run that follows reports this properly, with the engine name.
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
        // A process that was just stopped can still hand over a last line or two, and
        // those belong to a job that has already been answered.
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

            // Quiet from the start can also mean the reason hasn't arrived yet, so give it
            // one settle period whatever happened above.
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
                // LAPSE decodes in-process, but kill the tree anyway in case a build
                // ever starts a helper.
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
