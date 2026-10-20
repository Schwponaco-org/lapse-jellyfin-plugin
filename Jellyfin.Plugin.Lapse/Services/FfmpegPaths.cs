// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Small helpers for the ffmpeg/ffprobe calls.
/// </summary>
public static class FfmpegPaths
{
    /// <summary>
    /// Prefixes file: so ffmpeg never reads a name like "concat:x.mkv" as a protocol.
    /// (file: doesn't percent-decode, so %20 in a name stays %20.)
    /// </summary>
    public static string AsFileUrl(string path)
    {
        return "file:" + path;
    }

    /// <summary>
    /// Timeout for reading a whole file: 10 minutes + 1 per GB. A flat limit is either too
    /// short for a 60GB remux on a NAS or way too long for an episode.
    /// </summary>
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
    /// Runs ffmpeg and waits, killing it past the timeout.
    /// </summary>
    /// <exception cref="NotSupportedException">ffmpeg couldn't be started.</exception>
    /// <exception cref="TimeoutException">It ran past the limit and was killed.</exception>
    public static async Task<(int ExitCode, string Stderr)> RunAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpegPath, arguments, timeout, cancellationToken).ConfigureAwait(false);
            return (result.ExitCode, result.Stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NotSupportedException("Could not start ffmpeg: " + ex.Message, ex);
        }
    }
}
