// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
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
/// Pulls the subtitle tracks out of a video for good: writes each as a sidecar, then
/// rebuilds the container without them.
///
/// It's for direct play. Embedded tracks either get rendered by the client (the web
/// client's timing on those drifts, known annoyance) or burnt in, which means a transcode.
/// A file next to the video has neither problem.
///
/// Only tracks that were actually saved get dropped. PGS/VobSub can't be extracted as text
/// so they always stay. The video is copied with -c copy, never re-encoded.
/// </summary>
public class SubtitleRemuxer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromHours(2);

    // Containers we'll rewrite. Anything else is refused rather than remuxed into something
    // that quietly drops a track.
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
    public SubtitleRemuxer(IMediaEncoder mediaEncoder, SubtitleExtractor extractor, ILogger<SubtitleRemuxer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Writes an item's embedded tracks out as files and optionally rebuilds the video
    /// without them. Never called by anything automatic. Removing and replacing are separate
    /// opt-ins so you can keep the tracks, drop them into a copy, or drop them in place.
    /// </summary>
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

        // only the rebuild needs these, extraction alone works on any container
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

        // The rebuild drops streams by number and Jellyfin's numbers aren't the file's. So
        // find every track in the actual file first, and if any can't be pinned down for
        // sure (file replaced since the scan, no ffprobe), don't remove anything.
        var fileIndexes = new Dictionary<MediaStream, int>();

        if (removeFromVideo)
        {
            foreach (var track in tracks)
            {
                try
                {
                    var match = await _extractor.FindInFileAsync(item, track.Index, cancellationToken).ConfigureAwait(false);

                    if (!match.Verified)
                    {
                        result.Error = "LAPSE couldn't read the video file to check which of its streams are which, so nothing was removed.";
                        return result;
                    }

                    if (!match.JellyfinAgrees)
                    {
                        result.Error = "The subtitle tracks in this video don't match what Jellyfin has on record for it, so nothing was removed. Refresh the item's metadata in Jellyfin and try again.";
                        return result;
                    }

                    fileIndexes[track] = match.Track.Index;
                }
                catch (InvalidDataException)
                {
                    result.Error = "The subtitle tracks in this video don't match what Jellyfin has on record for it, so nothing was removed. Refresh the item's metadata in Jellyfin and try again.";
                    return result;
                }
            }
        }

        // save first, a track that won't come out stays in the video
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

        // extract only, video untouched
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
            await RunFfmpegAsync(videoPath, destination, removable.Select(t => fileIndexes[t]).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or TimeoutException)
        {
            TryDelete(destination);
            result.Error = ex.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            // stopped mid rebuild, original is untouched, drop the half written copy
            TryDelete(destination);
            throw;
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

        // Only now touch the original. Move it aside first instead of deleting, so a failed
        // move leaves both files rather than neither.
        var reprieve = videoPath + ".lapse-old";

        try
        {
            File.Move(videoPath, reprieve, overwrite: true);
            File.Move(destination, videoPath, overwrite: true);
            File.Delete(reprieve);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // put it back so the item still plays
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

    private async Task RunFfmpegAsync(
        string videoPath,
        string destination,
        List<int> removableIndexes,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-nostdin", "-y", "-i", FfmpegPaths.AsFileUrl(videoPath) };

        // map everything then un-map the removed tracks. Mapping only what we want would
        // lose chapters, attachments and stream types we don't know about.
        arguments.Add("-map");
        arguments.Add("0");

        foreach (var index in removableIndexes)
        {
            arguments.Add("-map");
            arguments.Add("-0:" + index.ToString(CultureInfo.InvariantCulture));
        }

        // straight copy, nothing decoded
        arguments.AddRange(new[] { "-c", "copy", "-map_metadata", "0", "-map_chapters", "0", FfmpegPaths.AsFileUrl(destination) });

        int exitCode;
        string stderr;

        try
        {
            (exitCode, stderr) = await FfmpegPaths.RunAsync(_mediaEncoder.EncoderPath, arguments, RemuxTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("ffmpeg took too long rebuilding that video file.", ex);
        }

        if (exitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
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

    // Movie.nosubs.mkv, also what's left when the original is kept
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

    private static void TryMoveBack(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing more we can do, the .lapse-old file is still there to rename by hand
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // untidy but not worth reporting
        }
    }
}
