// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// What a finished child process printed, and how it exited.
/// </summary>
public readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs a child process to the end. Args are passed one by one (no shell), both pipes are
/// drained so nothing stalls, output is read as UTF-8, and the whole tree gets killed on
/// timeout or cancel. Every engine/ffmpeg/ffprobe call goes through here.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs a program and waits for it.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    /// <exception cref="TimeoutException">It ran past the time limit and was killed.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled, and it was killed.</exception>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        Action<ProcessStartInfo>? prepare = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ProcessOutput.ReadAsUtf8(startInfo);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        prepare?.Invoke(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // these finish when the pipes close, which killing the process does too
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } after)
        {
            limit.CancelAfter(after);
        }

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancelling the wait doesn't stop the process, and engines start their own ffmpeg, so kill the tree
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{System.IO.Path.GetFileName(fileName)} took longer than {Describe(timeout!.Value)} and was stopped.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Kills a process and its children if still running. Never throws.
    /// </summary>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // finished on its own between the check and the kill
        }
    }

    private static string Describe(TimeSpan timeout)
    {
        return timeout.TotalMinutes >= 1
            ? Math.Round(timeout.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) + " minutes"
            : Math.Round(timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds";
    }
}
