// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Takes the subtitle tracks out of a video file for good: writes each one out as a
/// sidecar, then rebuilds the container without them.
///
/// The reason to want this is direct play. A subtitle track inside the video means clients
/// have to either render it themselves - and the Jellyfin web client's timing for embedded
/// tracks drifts against the video often enough to be a known annoyance - or fall back to
/// burning it in, which is a transcode and has to be turned on per client. The same
/// subtitle as a file beside the video has neither problem: every client reads it, the
/// timing is the file's own, and the video streams untouched.
///
/// Nothing is dropped that could not first be saved. Text tracks come out as .srt/.ass/.vtt
/// and are then removed; PGS and VobSub are pictures with no text to extract, so those
/// stay in the video however this is called. Losing a track to a tidy-up is not a trade
/// worth offering.
///
/// The video itself is only ever copied, never re-encoded: ffmpeg maps every stream except
/// the subtitles across with -c copy, so this is a container rewrite at disk speed and the
/// video and audio come out bit for bit identical.
/// </summary>
public class SubtitleRemuxer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// Containers ffmpeg can write back out with the streams a video like this carries.
    /// Anything else gets turned away rather than quietly remuxed into something that
    /// drops a track: mp4 in particular cannot hold an ASS subtitle or a FLAC audio track,
    /// and finding that out afterwards is too late.
    /// </summary>
    private static readonly HashSet<string> SupportedContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mka", ".webm", ".mp4", ".m4v", ".mov"
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly SubtitleExtractor _extractor;
    private readonly ILogger<SubtitleRemuxer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleRemuxer"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin ships.</param>
    /// <param name="extractor">Writes each track out before it's dropped.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleRemuxer(IMediaEncoder mediaEncoder, SubtitleExtractor extractor, ILogger<SubtitleRemuxer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Writes an item's embedded subtitle tracks out as files, and optionally rebuilds the
    /// video without them.
    ///
    /// Nothing here ever happens on its own. There is no scheduled task and no automation
    /// action that calls this: rewriting somebody's library files is a thing to be asked
    /// for each time, on the item it is wanted on. Both steps past the extraction are
    /// separately opted into, so the three useful answers - keep the tracks, drop them but
    /// keep the original video, drop them and replace it - are all reachable.
    /// </summary>
    /// <param name="item">The item to work on.</param>
    /// <param name="removeFromVideo">True to rebuild the video without the tracks that
    /// were extracted. False just writes the files and leaves the video alone, which is
    /// the default and undoes nothing.</param>
    /// <param name="replaceOriginal">True to put the rebuilt video in the original's
    /// place, false to leave the original alone and write a .nosubs file beside it. Only
    /// means anything when <paramref name="removeFromVideo"/> is set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Every path here comes from Jellyfin's own resolved path for an item the caller already looked up, or is derived from it. The request contributes an item id and two booleans, none of which reach a path.")]
    public async Task<RemuxResult> ExtractEmbeddedSubtitlesAsync(
        BaseItem item,
        bool removeFromVideo,
        bool replaceOriginal,
        CancellationToken cancellationToken = default)
    {
        var result = new RemuxResult { VideoPath = item.Path, ReplacedOriginal = false };

        var videoPath = item.Path;
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            result.Error = "That item has no video file to work on.";
            return result;
        }

        // Both of these only stand in the way of the rebuild. Extracting on its own needs
        // neither, so a container LAPSE won't rewrite still gets its subtitles as files.
        if (removeFromVideo)
        {
            if (!HasFfmpeg())
            {
                result.Error = "The server hasn't told the plugin where its ffmpeg is, and rebuilding a video file needs it.";
                return result;
            }

            var container = Path.GetExtension(videoPath);
            if (!SupportedContainers.Contains(container))
            {
                result.Error = $"LAPSE doesn't rebuild {container} files. Only mkv, mp4, mov and webm can be written back out with their streams intact.";
                return result;
            }
        }

        var tracks = item.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && string.IsNullOrEmpty(s.Path))
            .ToList();

        if (tracks.Count == 0)
        {
            result.Error = "There are no subtitle tracks inside that video file.";
            return result;
        }

        // Everything that can be saved gets saved first. A track that won't come out is
        // left in the video rather than being dropped with it.
        var removable = new List<MediaStream>();

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_extractor.GetExtractionProblem(track.Codec) is not null)
            {
                result.KeptTracks.Add(Describe(track));
                continue;
            }

            try
            {
                var path = await _extractor
                    .ExtractAsync(item, track.Index, track.Codec, track.Language, cancellationToken)
                    .ConfigureAwait(false);

                result.ExtractedPaths.Add(path);
                removable.Add(track);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not extract subtitle stream {Index} of {Video}, so it stays in the video",
                    track.Index,
                    videoPath);

                result.KeptTracks.Add(Describe(track));
            }
        }

        if (removable.Count == 0)
        {
            result.Error = result.KeptTracks.Count > 0
                ? "None of those tracks could be saved as a subtitle file. Picture based subtitles (PGS, VobSub) need OCR first."
                : "Nothing could be extracted from that video.";
            return result;
        }

        // Extract only: the files are written, the video is untouched, and every track is
        // still in it. This is what the action does unless removal was asked for.
        if (!removeFromVideo)
        {
            result.Success = true;

            _logger.LogInformation(
                "Extracted {Count} subtitle track(s) from {Video} to files beside it, leaving the video alone",
                result.ExtractedPaths.Count,
                videoPath);

            return result;
        }

        var destination = BuildDestination(videoPath);

        try
        {
            await RunFfmpegAsync(videoPath, destination, removable, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or TimeoutException)
        {
            TryDelete(destination);
            result.Error = ex.Message;
            return result;
        }

        result.RemovedCount = removable.Count;

        if (!replaceOriginal)
        {
            result.VideoPath = destination;
            result.Success = true;

            _logger.LogInformation(
                "Wrote {Destination} without {Count} subtitle track(s), leaving {Video} alone",
                destination,
                removable.Count,
                videoPath);

            return result;
        }

        // Only now, with a complete file on disk that ffmpeg exited cleanly on, does the
        // original get touched. It goes to one side first rather than being deleted, so a
        // failed move leaves both files rather than neither.
        var reprieve = videoPath + ".lapse-old";

        try
        {
            File.Move(videoPath, reprieve, overwrite: true);
            File.Move(destination, videoPath, overwrite: true);
            File.Delete(reprieve);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put back whatever got as far as moving, so the item is still playable.
            if (!File.Exists(videoPath) && File.Exists(reprieve))
            {
                TryMoveBack(reprieve, videoPath);
            }

            _logger.LogError(ex, "Could not put the rebuilt video in place of {Video}", videoPath);

            result.VideoPath = File.Exists(destination) ? destination : videoPath;
            result.Error = "The video was rebuilt, but it couldn't be moved into place: " + ex.Message +
                " The rebuilt file is still there under its own name.";
            return result;
        }

        result.VideoPath = videoPath;
        result.ReplacedOriginal = true;
        result.Success = true;

        _logger.LogInformation(
            "Rebuilt {Video} without {Count} subtitle track(s), which are now files beside it",
            videoPath,
            removable.Count);

        return result;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path for an item the caller already looked up, the destination is derived from it, and the stream indexes come off the item's own media streams. Nothing from the request reaches any of it.")]
    private async Task RunFfmpegAsync(
        string videoPath,
        string destination,
        List<MediaStream> removable,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);

        // Take everything, then put back the tracks being removed. Mapping the wanted
        // streams instead would silently drop chapters, attachments and any stream type
        // this code doesn't know to ask for.
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0");

        foreach (var track in removable)
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("-0:" + track.Index.ToString(CultureInfo.InvariantCulture));
        }

        // Straight copy. No stream is decoded, so this runs at disk speed and the video
        // comes out identical to what went in.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-map_chapters");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add(destination);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NotSupportedException("Could not start ffmpeg to rebuild that video: " + ex.Message, ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RemuxTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("ffmpeg took too long rebuilding that video file.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            _logger.LogWarning("ffmpeg could not rebuild {Video} without its subtitles: {Error}", videoPath, stderr);
            throw new InvalidDataException("ffmpeg couldn't rebuild that video file without its subtitle tracks.");
        }
    }

    private bool HasFfmpeg()
    {
        var path = _mediaEncoder.EncoderPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string Describe(MediaStream stream)
    {
        var language = string.IsNullOrWhiteSpace(stream.Language) ? "unknown language" : stream.Language;
        var codec = string.IsNullOrWhiteSpace(stream.Codec) ? "unknown format" : stream.Codec.ToUpperInvariant();

        return string.Format(
            CultureInfo.InvariantCulture,
            "Track {0} ({1}, {2})",
            stream.Index,
            language,
            codec);
    }

    // The working file, which is also what's left behind when the original is being kept.
    // .nosubs sits before the extension so the container stays what it was.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Built from Jellyfin's own resolved video path. Nothing from the request reaches it.")]
    private static string BuildDestination(string videoPath)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var extension = Path.GetExtension(videoPath);

        var destination = Path.Combine(folder, stem + ".nosubs" + extension);

        var attempt = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(
                folder,
                stem + ".nosubs." + attempt.ToString(CultureInfo.InvariantCulture) + extension);
            attempt++;
        }

        return destination;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // finished on its own in the meantime
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever moves the file this class set aside back to the path it came from.")]
    private static void TryMoveBack(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing further can be done here; the caller already reports the failure and
            // the set aside file keeps its .lapse-old name for someone to rename by hand.
            _ = ex;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever removes the half written output file this class just named.")]
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // untidy, not a failure worth reporting over the one that actually went wrong
        }
    }
}
