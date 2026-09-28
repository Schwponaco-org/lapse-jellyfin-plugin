// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// The small pieces every ffmpeg and ffprobe call in the subtitle extraction needs.
/// </summary>
public static class FfmpegPaths
{
    /// <summary>
    /// Turns a path into something ffmpeg only ever reads as a file.
    ///
    /// ffmpeg picks a protocol off anything before the first colon, so a file called
    /// "concat:something.mkv" or "subfile,,..." sitting in a library is not opened as the
    /// file it is. file: settles it. It does no percent decoding, so a name with %20 in it
    /// still means exactly that name.
    /// </summary>
    /// <param name="path">A local path.</param>
    /// <returns>The path with file: in front.</returns>
    public static string AsFileUrl(string path)
    {
        return "file:" + path;
    }

    /// <summary>
    /// How long reading a whole file for its subtitles may take before it counts as stuck.
    ///
    /// A flat limit is either too short for a 60 GB remux on a NAS or far too long for a
    /// TV episode. ffmpeg reads the whole file to find every subtitle packet, so the
    /// allowance grows with the size: ten minutes, plus a minute for every gigabyte,
    /// which still lets a share that only manages ~20 MB/s finish.
    /// </summary>
    /// <param name="videoPath">The file ffmpeg is about to read.</param>
    /// <returns>The time limit.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only reads the size of a video file Jellyfin already resolved.")]
    public static TimeSpan ReadTimeoutFor(string videoPath)
    {
        long bytes;

        try
        {
            bytes = new FileInfo(videoPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            bytes = 0;
        }

        var gigabytes = bytes / (1024.0 * 1024 * 1024);
        return TimeSpan.FromMinutes(10 + Math.Ceiling(gigabytes));
    }

    /// <summary>
    /// Runs ffmpeg with the arguments given and waits for it, killing it if it runs past
    /// the time limit.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpeg binary.</param>
    /// <param name="arguments">Its arguments, one per entry, never through a shell.</param>
    /// <param name="timeout">How long it gets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code and whatever it printed to stderr.</returns>
    /// <exception cref="NotSupportedException">ffmpeg couldn't be started.</exception>
    /// <exception cref="TimeoutException">It ran past the limit and was killed.</exception>
    public static async Task<(int ExitCode, string Stderr)> RunAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
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

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NotSupportedException("Could not start ffmpeg: " + ex.Message, ex);
        }

        // Both pipes get drained, or a chatty ffmpeg fills one and waits on it forever.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("ffmpeg took longer than " + timeout.TotalMinutes + " minutes and was stopped.");
        }

        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// Kills a process and everything it started, if it's still running.
    /// </summary>
    /// <param name="process">The process.</param>
    public static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // finished on its own in the meantime
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // already on its way out
        }
    }
}
