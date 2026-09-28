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
/// How one track's extraction went.
/// </summary>
public enum TrackExtractionOutcome
{
    /// <summary>
    /// The file was written.
    /// </summary>
    Written,

    /// <summary>
    /// The track had nothing in it. Some releases carry a forced track as an empty
    /// placeholder; there's no file to write for one of those.
    /// </summary>
    Empty,

    /// <summary>
    /// ffmpeg couldn't get it out.
    /// </summary>
    Failed,

    /// <summary>
    /// Something else wrote the file while this was running, and it was left alone.
    /// </summary>
    LeftAlone
}

/// <summary>
/// One track to pull out of a video, and where it goes.
/// </summary>
/// <param name="Track">The track, as the file describes it.</param>
/// <param name="Destination">Where the finished subtitle goes.</param>
/// <param name="ReplaceExisting">True when a file already at the destination is known to
/// be out of date and should be replaced. The file's write time at the moment that was
/// decided goes in <paramref name="ExistingWriteTimeUtc"/>, and it's only replaced if
/// nothing has touched it since.</param>
/// <param name="ExistingWriteTimeUtc">See <paramref name="ReplaceExisting"/>.</param>
public sealed record TrackExtractionJob(
    EmbeddedSubtitleTrack Track,
    string Destination,
    bool ReplaceExisting = false,
    DateTime? ExistingWriteTimeUtc = null);

/// <summary>
/// What became of one <see cref="TrackExtractionJob"/>.
/// </summary>
/// <param name="Job">The job.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Error">Why, when it failed.</param>
public sealed record TrackExtractionResult(TrackExtractionJob Job, TrackExtractionOutcome Outcome, string? Error = null);

/// <summary>
/// Pulls a subtitle track out of the video file it's baked into and writes it next to the
/// video as an ordinary sidecar.
///
/// Most films only ever have embedded subtitles - they came in the mkv and nobody ever
/// put an .srt beside them - and an engine can't read a track that only exists inside a
/// container. Extracting one turns it into a normal subtitle file that syncing, shifting,
/// converting and translating all work on, and that Jellyfin picks up as another track on
/// its next scan, so nothing is lost by doing it.
///
/// Only the text based tracks can come out this way for syncing. PGS and VobSub are
/// sequences of pictures with no characters in them at all. The scheduled extraction can
/// copy PGS out as .sup when asked, but that is a straight copy for Jellyfin to show, not
/// something the rest of the plugin can edit.
///
/// A single track asked for by hand is pulled out with Jellyfin's own subtitle encoder
/// first, the same one the server uses when a client asks for an embedded track. It keeps
/// a cached copy, so a track that's already been played usually comes back without
/// touching the video at all. Calling ffmpeg directly is the fallback, and the only route
/// the scheduled extraction uses: Jellyfin's encoder extracts every subtitle in the file
/// at once into its cache, which is exactly the work a forced-English-only run is trying
/// not to do, and one empty track in that batch fails all of them.
///
/// Before any of that, the file itself is asked which track is at that index. Jellyfin's
/// database says what the file held when it was last scanned, and a file replaced since
/// can have something else entirely there.
/// </summary>
public class SubtitleExtractor
{
    /// <summary>
    /// The prefix that marks a subtitle option as one still inside the video file. The
    /// number after it is the stream index ffmpeg and Jellyfin both use.
    /// </summary>
    public const string EmbeddedPrefix = "embedded://";

    // What each text subtitle codec should land on disk as. Anything not in here either
    // isn't text or isn't something the plugin can work with afterwards.
    private static readonly Dictionary<string, string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subrip"] = ".srt",
        ["srt"] = ".srt",
        ["text"] = ".srt",
        ["mov_text"] = ".srt",
        ["ass"] = ".ass",
        ["ssa"] = ".ssa",
        ["webvtt"] = ".vtt",
        ["vtt"] = ".vtt"
    };

    // Picture codecs that can still be copied out whole. Only PGS has a muxer ffmpeg can
    // write on its own; VobSub needs an .idx beside the .sub and DVB has no file format.
    private static readonly Dictionary<string, string> PictureCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hdmv_pgs_subtitle"] = ".sup",
        ["pgssub"] = ".sup"
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly EmbeddedSubtitleProbe _probe;
    private readonly SubtitleLanguages _languages;
    private readonly ILogger<SubtitleExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleExtractor"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin ships.</param>
    /// <param name="subtitleEncoder">Jellyfin's own extractor, used before ffmpeg by hand.</param>
    /// <param name="probe">Asks the file which track is really at an index.</param>
    /// <param name="languages">Compares language tags.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleExtractor(
        IMediaEncoder mediaEncoder,
        ISubtitleEncoder subtitleEncoder,
        EmbeddedSubtitleProbe probe,
        SubtitleLanguages languages,
        ILogger<SubtitleExtractor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _subtitleEncoder = subtitleEncoder;
        _probe = probe;
        _languages = languages;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a subtitle option refers to a track inside the video rather than to a
    /// file on disk.
    /// </summary>
    /// <param name="path">The option's path.</param>
    /// <returns>True for an embedded track.</returns>
    public static bool IsEmbedded(string? path)
    {
        return path is not null && path.StartsWith(EmbeddedPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the identifier used in place of a path for an embedded track.
    /// </summary>
    /// <param name="streamIndex">The stream's index in the container.</param>
    /// <returns>The identifier.</returns>
    public static string BuildKey(int streamIndex)
    {
        return EmbeddedPrefix + streamIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the stream index back out of an embedded track's identifier.
    /// </summary>
    /// <param name="path">The identifier.</param>
    /// <param name="streamIndex">The index.</param>
    /// <returns>True if it was one of ours and held a number.</returns>
    public static bool TryParseKey(string? path, out int streamIndex)
    {
        streamIndex = -1;

        return IsEmbedded(path)
            && int.TryParse(path![EmbeddedPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out streamIndex);
    }

    /// <summary>
    /// Gets the file extension a subtitle codec should be written as, or null when that
    /// codec holds pictures rather than text.
    /// </summary>
    /// <param name="codec">The codec name Jellyfin reported.</param>
    /// <returns>An extension including the dot, or null.</returns>
    public static string? GetExtensionForCodec(string? codec)
    {
        return codec is not null && TextCodecs.TryGetValue(codec, out var extension) ? extension : null;
    }

    /// <summary>
    /// Gets the extension a track is written as, allowing picture tracks that can be
    /// copied out whole when asked to.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    /// <param name="allowPictures">True to allow PGS out as .sup.</param>
    /// <returns>An extension including the dot, or null when the track can't be written.</returns>
    public static string? GetExtensionForCodec(string? codec, bool allowPictures)
    {
        if (GetExtensionForCodec(codec) is { } text)
        {
            return text;
        }

        return allowPictures && codec is not null && PictureCodecs.TryGetValue(codec, out var picture) ? picture : null;
    }

    /// <summary>
    /// Says why a track can't be extracted, or null when it can be.
    /// </summary>
    /// <param name="codec">The codec name Jellyfin reported.</param>
    /// <returns>A message for the caller, or null.</returns>
    public string? GetExtractionProblem(string? codec)
    {
        if (GetExtensionForCodec(codec) is null)
        {
            return $"{(codec ?? "That").ToUpperInvariant()} subtitles are pictures of text rather than text, so there's nothing to extract or line up. They need OCR first, with something like Subtitle Edit.";
        }

        return null;
    }

    /// <summary>
    /// Says whether a subtitle file is an extracted copy of one of the item's embedded
    /// tracks that nobody has synced since.
    ///
    /// Background runs leave embedded tracks alone on purpose - they came with the release
    /// and are usually right already - and an untouched copy of one is the same track in a
    /// different place. Treating it as a new subtitle to sync would have every nightly run
    /// churn through whatever the scheduled extraction wrote, forced tracks of three lines
    /// included, which is exactly the kind of track an engine is least sure about. Once
    /// somebody has synced it by hand it's their subtitle like any other.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>True for an extracted track with no sync on record.</returns>
    public static bool IsUntouchedExtraction(BaseItem item, string? subtitlePath)
    {
        if (!ExtractedSubtitleNames.IsExtractedTrack(item.Path, subtitlePath))
        {
            return false;
        }

        return !HasSyncRecord(item.Id, subtitlePath!);
    }

    /// <summary>
    /// Says whether an extracted subtitle has anything in it: at least one cue for text,
    /// at least one PGS segment for .sup. An empty embedded track still comes out as a
    /// file - a zero byte .srt, or an .ass that is all header - and that is no subtitle.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="extension">What it was written as.</param>
    /// <returns>True when it holds at least one subtitle.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever reads a file this class just wrote.")]
    public static bool HasContent(string path, string extension)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (string.Equals(extension, ".sup", StringComparison.OrdinalIgnoreCase))
        {
            // Every PGS segment starts with the two bytes "PG".
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.ReadByte() == 'P' && stream.ReadByte() == 'G';
        }

        var ass = string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase);

        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (ass
                ? line.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
                : line.Contains("-->", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies one subtitle track out of a video into a file beside it, asking Jellyfin for
    /// the track first and only falling back to ffmpeg if the server can't hand it over.
    /// </summary>
    /// <param name="item">The item holding the track.</param>
    /// <param name="streamIndex">The track's stream index as Jellyfin has it.</param>
    /// <param name="codec">The track's codec, which decides the file extension.</param>
    /// <param name="language">The track's language, used in the file name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file that was written.</returns>
    /// <exception cref="NotSupportedException">The track isn't text, or ffmpeg is missing.</exception>
    /// <exception cref="InvalidDataException">Neither route could produce a subtitle.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path for an item the caller already looked up, and the destination is built from it by ExtractedSubtitleNames. The request contributes an item id and a stream index, neither of which reaches a path as text.")]
    public async Task<string> ExtractAsync(
        BaseItem item,
        int streamIndex,
        string? codec,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (GetExtractionProblem(codec) is { } problem)
        {
            throw new NotSupportedException(problem);
        }

        var videoPath = item.Path;
        var extension = GetExtensionForCodec(codec)!;

        // Jellyfin's number for the track isn't the file's, so find the track in the file.
        // If that can't be told for certain, stop rather than write some other track out
        // under this one's name.
        var match = await FindInFileAsync(item, streamIndex, cancellationToken).ConfigureAwait(false);
        var track = match.Track;
        var indexMoved = !match.JellyfinAgrees;

        if (track.Codec is null)
        {
            track.Codec = codec;
        }

        track.Language ??= language;

        var destination = ExtractedSubtitleNames.BuildPath(
            videoPath, track.Index, track.Language, track.IsForced, track.IsHearingImpaired, extension);

        if (!ExtractedSubtitleNames.FitsFileSystem(destination))
        {
            throw new IOException("The video's file name is too long to put a subtitle beside it with the language and track number added.");
        }

        // The same track pulled out twice is the same file, so a second sync of it reuses
        // what the first one wrote rather than extracting again.
        if (IsCurrent(item, destination))
        {
            _logger.LogDebug(
                "Subtitle stream {Index} of {Video} is already extracted at {Destination}, reusing it",
                track.Index,
                videoPath,
                destination);

            return destination;
        }

        // Jellyfin's extractor first, but only while its number for the track is right:
        // it goes by that number, and a wrong one hands back a different track.
        if (!indexMoved)
        {
            var native = await TryReadNativeAsync(item, streamIndex, extension, cancellationToken).ConfigureAwait(false);
            if (native is not null
                && await TryWriteNativeAsync(native, destination, extension, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Extracted subtitle stream {Index} from {Video} to {Destination} using Jellyfin's extractor",
                    track.Index,
                    videoPath,
                    destination);

                return destination;
            }
        }

        var job = new TrackExtractionJob(track, destination, ReplaceExisting: File.Exists(destination), ExistingWriteTimeUtc: WriteTimeOrNull(destination));
        var result = (await ExtractTracksAsync(videoPath, new[] { job }, allowPictures: false, cancellationToken).ConfigureAwait(false))[0];

        return result.Outcome switch
        {
            TrackExtractionOutcome.Written or TrackExtractionOutcome.LeftAlone => destination,
            TrackExtractionOutcome.Empty => throw new InvalidDataException("That subtitle track is empty. Some releases carry one as a placeholder with nothing in it."),
            _ => throw new InvalidDataException(result.Error ?? "ffmpeg couldn't get that subtitle track out of the video file.")
        };
    }

    /// <summary>
    /// Pulls several tracks out of one video with ffmpeg, reading the file once.
    ///
    /// ffmpeg takes one input and any number of outputs, so every wanted track comes out
    /// of the same pass over the file. The catch is that one output ffmpeg can't write
    /// fails the whole run, so when a combined run fails each track gets its own run and
    /// its own answer. An empty track isn't a failure at all: ffmpeg writes an empty file
    /// for it and carries on, and that file is simply thrown away.
    ///
    /// Every file is written under a temporary name and only moved into place once it is
    /// complete and has something in it, so a run that dies half way never leaves a file
    /// that looks finished.
    /// </summary>
    /// <param name="videoPath">The video file.</param>
    /// <param name="jobs">The tracks and where each goes.</param>
    /// <param name="allowPictures">True to allow PGS tracks out as .sup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per job, in the same order.</returns>
    public async Task<IReadOnlyList<TrackExtractionResult>> ExtractTracksAsync(
        string videoPath,
        IReadOnlyList<TrackExtractionJob> jobs,
        bool allowPictures,
        CancellationToken cancellationToken)
    {
        var results = new TrackExtractionResult?[jobs.Count];
        var runnable = new List<int>();

        for (var i = 0; i < jobs.Count; i++)
        {
            if (GetExtensionForCodec(jobs[i].Track.Codec, allowPictures) is null)
            {
                results[i] = new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "LAPSE can't write that kind of subtitle track out as a file.");
            }
            else
            {
                runnable.Add(i);
            }
        }

        if (runnable.Count > 0 && !HasFfmpeg())
        {
            foreach (var i in runnable)
            {
                results[i] = new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "The server hasn't told the plugin where its ffmpeg is.");
            }

            runnable.Clear();
        }

        if (runnable.Count > 0)
        {
            var combined = await RunAsync(videoPath, runnable.Select(i => jobs[i]).ToList(), cancellationToken).ConfigureAwait(false);

            // Only a combined run of more than one track is worth splitting up after a
            // failure. A timeout isn't: if reading the file once took too long, reading it
            // once per track won't go better.
            if (combined is null && runnable.Count > 1)
            {
                _logger.LogInformation(
                    "Extracting {Count} subtitle tracks from {Video} in one go failed, trying them one at a time",
                    runnable.Count,
                    videoPath);

                foreach (var i in runnable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var single = await RunAsync(videoPath, new[] { jobs[i] }, cancellationToken).ConfigureAwait(false);
                    results[i] = single?[0] ?? new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "ffmpeg couldn't get that subtitle track out of the video file.");
                }
            }
            else
            {
                for (var n = 0; n < runnable.Count; n++)
                {
                    var i = runnable[n];
                    results[i] = combined?[n] ?? new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "ffmpeg couldn't get that subtitle track out of the video file.");
                }
            }
        }

        return results.Select(r => r!).ToList();
    }

    // One ffmpeg run over the file for all of the given jobs. Null means ffmpeg itself
    // failed and nothing it wrote can be trusted; otherwise there's a result per job.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path, and every destination was built from it by ExtractedSubtitleNames.")]
    private async Task<List<TrackExtractionResult>?> RunAsync(
        string videoPath,
        IReadOnlyList<TrackExtractionJob> jobs,
        CancellationToken cancellationToken)
    {
        var temps = jobs.Select(j => ExtractedSubtitleNames.TempPathFor(j.Destination)).ToList();

        // -copyts keeps the timestamps the container has, the same as Jellyfin's own
        // single track extraction and mkvextract. Without it ffmpeg shifts everything by
        // the file's start time, which for a video with B-frames is a few tens of
        // milliseconds of drift built into a subtitle a sync tool then has to take out.
        var arguments = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            FfmpegPaths.AsFileUrl(videoPath),
            "-copyts"
        };

        for (var i = 0; i < jobs.Count; i++)
        {
            var (muxer, codecArgument) = PlanOutput(jobs[i].Track.Codec);

            arguments.Add("-map");
            arguments.Add("0:" + jobs[i].Track.Index.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-c:s");
            arguments.Add(codecArgument);

            // Name the muxer rather than leaving ffmpeg to guess it from the file name,
            // which on a temporary name it can't.
            arguments.Add("-f");
            arguments.Add(muxer);
            arguments.Add(FfmpegPaths.AsFileUrl(temps[i]));
        }

        try
        {
            int exitCode;
            string stderr;

            try
            {
                (exitCode, stderr) = await FfmpegPaths
                    .RunAsync(_mediaEncoder.EncoderPath, arguments, FfmpegPaths.ReadTimeoutFor(videoPath), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning("ffmpeg timed out extracting subtitles from {Video}", videoPath);
                return jobs.Select(j => new TrackExtractionResult(j, TrackExtractionOutcome.Failed, ex.Message)).ToList();
            }
            catch (NotSupportedException ex)
            {
                return jobs.Select(j => new TrackExtractionResult(j, TrackExtractionOutcome.Failed, ex.Message)).ToList();
            }

            if (exitCode != 0)
            {
                _logger.LogWarning(
                    "ffmpeg could not extract subtitle stream(s) {Indexes} from {Video}: {Error}",
                    string.Join(", ", jobs.Select(j => j.Track.Index)),
                    videoPath,
                    stderr.Trim());

                return null;
            }

            var results = new List<TrackExtractionResult>(jobs.Count);

            for (var i = 0; i < jobs.Count; i++)
            {
                results.Add(await PlaceAsync(videoPath, jobs[i], temps[i], cancellationToken).ConfigureAwait(false));
            }

            return results;
        }
        finally
        {
            foreach (var temp in temps)
            {
                TryDelete(temp);
            }
        }
    }

    // Moves a finished temporary file into place, if it has anything in it and the
    // destination is still free to take it.
    private async Task<TrackExtractionResult> PlaceAsync(
        string videoPath,
        TrackExtractionJob job,
        string temp,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(job.Destination);

        bool hasContent;
        try
        {
            hasContent = HasContent(temp, extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TrackExtractionResult(job, TrackExtractionOutcome.Failed, ex.Message);
        }

        if (!hasContent)
        {
            _logger.LogInformation(
                "Subtitle stream {Index} of {Video} is empty, so there's nothing to write",
                job.Track.Index,
                videoPath);

            return new TrackExtractionResult(job, TrackExtractionOutcome.Empty);
        }

        // Taken for the move so a sync or shift writing the same file can't interleave
        // with it, and checked again inside: if the file appeared or changed while ffmpeg
        // ran, whoever wrote it wins.
        using (await SubtitleFileLock.AcquireAsync(job.Destination, cancellationToken).ConfigureAwait(false))
        {
            var current = WriteTimeOrNull(job.Destination);

            if (current is not null && (!job.ReplaceExisting || current != job.ExistingWriteTimeUtc))
            {
                _logger.LogInformation(
                    "{Destination} was written by something else while subtitle stream {Index} was being extracted, so it was left as it is",
                    job.Destination,
                    job.Track.Index);

                return new TrackExtractionResult(job, TrackExtractionOutcome.LeftAlone);
            }

            try
            {
                File.Move(temp, job.Destination, overwrite: current is not null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TrackExtractionResult(job, TrackExtractionOutcome.Failed, ex.Message);
            }
        }

        _logger.LogInformation(
            "Extracted subtitle stream {Index} from {Video} to {Destination}",
            job.Track.Index,
            videoPath,
            job.Destination);

        return new TrackExtractionResult(job, TrackExtractionOutcome.Written);
    }

    // What ffmpeg writes each codec as. Tracks already in the format they're going out as
    // are copied byte for byte, which keeps an ass track's styling exactly as it was.
    // mov_text and plain text have no muxer of their own and are turned into srt.
    private static (string Muxer, string CodecArgument) PlanOutput(string? codec)
    {
        return (codec ?? string.Empty).ToLowerInvariant() switch
        {
            "subrip" or "srt" => ("srt", "copy"),
            "ass" => ("ass", "copy"),
            "ssa" => ("ass", "copy"),
            "webvtt" or "vtt" => ("webvtt", "webvtt"),
            "hdmv_pgs_subtitle" or "pgssub" => ("sup", "copy"),
            _ => ("srt", "srt")
        };
    }

    /// <summary>
    /// Finds one of Jellyfin's embedded subtitle streams in the video file itself.
    ///
    /// Jellyfin's number for a stream is not the one ffmpeg uses. It lists the subtitle
    /// files it found beside the video first and counts the file's own streams after
    /// them, so an item with two external subtitles has every embedded stream two higher
    /// than in the file. That is taken off first; then the file is asked what's at that
    /// number, and it has to agree on codec, language, title and the forced flag. If it
    /// doesn't - the file was replaced since Jellyfin scanned it - the one stream in the
    /// file that does agree is used, and if there isn't exactly one, nothing is.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="jellyfinIndex">The stream's index as Jellyfin has it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The track as the file has it, and whether Jellyfin's record of it holds.
    /// Without ffprobe the file can't be asked, and the answer is worked out from
    /// Jellyfin's numbers alone.</returns>
    /// <exception cref="InvalidDataException">The file doesn't hold the track any more, or
    /// holds more than one that could be it.</exception>
    public async Task<(EmbeddedSubtitleTrack Track, bool JellyfinAgrees, bool Verified)> FindInFileAsync(
        BaseItem item,
        int jellyfinIndex,
        CancellationToken cancellationToken)
    {
        var streams = item.GetMediaStreams();
        var known = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && !IsExternalStream(s) && s.Index == jellyfinIndex);

        var expected = new EmbeddedSubtitleTrack
        {
            Index = jellyfinIndex,
            Codec = known?.Codec,
            Language = known?.Language,
            Title = known?.Title,
            ForcedFlag = known?.IsForced ?? false,
            HearingImpairedFlag = known?.IsHearingImpaired ?? false
        };

        var fileIndex = ToFileIndex(streams, jellyfinIndex);

        var probed = await _probe.ProbeAsync(item.Path, cancellationToken).ConfigureAwait(false);
        if (probed is null)
        {
            expected.Index = fileIndex;
            return (expected, true, false);
        }

        var atIndex = probed.FirstOrDefault(t => t.Index == fileIndex);
        if (atIndex is not null && Agrees(atIndex, expected))
        {
            return (Merge(atIndex, expected), true, true);
        }

        var candidates = probed.Where(t => Agrees(t, expected)).ToList();
        if (candidates.Count == 1)
        {
            _logger.LogWarning(
                "Subtitle stream {Expected} of {Video} isn't where Jellyfin has it - the file has changed since it was scanned. Using stream {Actual}, which matches it",
                fileIndex,
                item.Path,
                candidates[0].Index);

            return (Merge(candidates[0], expected), false, true);
        }

        throw new InvalidDataException(
            "The subtitle tracks in this video don't match what Jellyfin has on record for it, so LAPSE can't tell which one to take. " +
            "Refresh the item's metadata in Jellyfin and try again.");
    }

    /// <summary>
    /// Turns Jellyfin's index for an embedded stream into the file's own, by taking off
    /// the external subtitles Jellyfin numbered ahead of it.
    /// </summary>
    /// <param name="streams">All of the item's streams, as Jellyfin has them.</param>
    /// <param name="jellyfinIndex">Jellyfin's index for the stream.</param>
    /// <returns>The index ffmpeg knows it by, if Jellyfin's record is current.</returns>
    public static int ToFileIndex(IEnumerable<MediaStream> streams, int jellyfinIndex)
    {
        return jellyfinIndex - streams.Count(s => IsExternalStream(s) && s.Index < jellyfinIndex);
    }

    private static bool IsExternalStream(MediaStream stream)
    {
        return stream.IsExternal || !string.IsNullOrEmpty(stream.Path);
    }

    // The file's own answer wins on everything it has; Jellyfin's title is kept when the
    // file has none, since a title is what the forced and sdh guesses read.
    private static EmbeddedSubtitleTrack Merge(EmbeddedSubtitleTrack probed, EmbeddedSubtitleTrack known)
    {
        return new EmbeddedSubtitleTrack
        {
            Index = probed.Index,
            Codec = probed.Codec ?? known.Codec,
            Language = probed.Language ?? known.Language,
            Title = probed.Title ?? known.Title,
            ForcedFlag = probed.ForcedFlag,
            HearingImpairedFlag = probed.HearingImpairedFlag
        };
    }

    // Whether a track in the file is the one Jellyfin described. Codec, language and the
    // forced flag have to agree, and the title too when Jellyfin has one, since that's
    // what tells two English tracks apart when a replaced file has them in a new order.
    private bool Agrees(EmbeddedSubtitleTrack probed, EmbeddedSubtitleTrack expected)
    {
        if (!string.Equals(CodecFamily(probed.Codec), CodecFamily(expected.Codec), StringComparison.Ordinal))
        {
            return false;
        }

        // A missing tag on either side doesn't contradict anything.
        if (!string.IsNullOrWhiteSpace(probed.Language)
            && !string.IsNullOrWhiteSpace(expected.Language)
            && !SubtitleLanguages.SameLanguage(_languages.Resolve(probed.Language), _languages.Resolve(expected.Language)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expected.Title)
            && !string.Equals(probed.Title?.Trim(), expected.Title.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return probed.ForcedFlag == expected.ForcedFlag;
    }

    private static string CodecFamily(string? codec)
    {
        return (codec ?? string.Empty).ToLowerInvariant() switch
        {
            "srt" or "subrip" => "subrip",
            "ssa" or "ass" => "ass",
            "vtt" or "webvtt" => "webvtt",
            "pgssub" or "hdmv_pgs_subtitle" => "pgs",
            var other => other
        };
    }

    /// <summary>
    /// Says whether an extracted file can be used as it is: it exists, it isn't empty, and
    /// it isn't a leftover from a video that has since been replaced. A file somebody has
    /// synced counts as current whatever its age, since it's their work now and not a copy
    /// to be refreshed.
    /// </summary>
    /// <param name="item">The item the file belongs to.</param>
    /// <param name="path">The extracted file.</param>
    /// <returns>True when it's fine to use.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only looks at paths ExtractedSubtitleNames built from Jellyfin's own resolved video path.")]
    public static bool IsCurrent(BaseItem item, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        return !IsOlderThanVideo(item.Path, info) || HasSyncRecord(item.Id, path);
    }

    /// <summary>
    /// Says whether a file was written before the video it came from was last changed.
    /// When a video is replaced by an upgrade under the same name, subtitles extracted
    /// from the old one would otherwise sit there forever looking current.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="extracted">The extracted file.</param>
    /// <returns>True when the video is newer.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path.")]
    public static bool IsOlderThanVideo(string videoPath, FileInfo extracted)
    {
        try
        {
            var video = new FileInfo(videoPath);

            // Two seconds of slack for filesystems that keep times coarsely (FAT, some
            // SMB shares), so a copy made in the same second isn't taken as older.
            return video.Exists && extracted.LastWriteTimeUtc < video.LastWriteTimeUtc - TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasSyncRecord(Guid itemId, string path)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        lock (Plugin.ConfigurationLock)
        {
            var record = config.MovieRecords.FirstOrDefault(r => r.ItemId == itemId);
            return record is not null
                && record.SyncedSubtitles.Any(s => string.Equals(s.Path, path, StringComparison.Ordinal));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only looks at a destination ExtractedSubtitleNames built.")]
    private static DateTime? WriteTimeOrNull(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Jellyfin hands the track back as a stream, converting it to the format we ask for if
    // the cached copy is in another one. Null means it couldn't, and ffmpeg gets its turn -
    // there's no reason to fail the whole thing while a second route is untried.
    private async Task<Stream?> TryReadNativeAsync(
        BaseItem item,
        int streamIndex,
        string extension,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _subtitleEncoder.GetSubtitles(
                item,
                item.Id.ToString("N", CultureInfo.InvariantCulture),
                streamIndex,
                extension.TrimStart('.'),
                0,
                0,
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Jellyfin's extractor couldn't hand over subtitle stream {Index} of {Item}, falling back to ffmpeg",
                streamIndex,
                item.Path);

            return null;
        }
    }

    // Writes what Jellyfin handed over, through a temporary file like everything else, and
    // only keeps it if there's a subtitle in it. An empty answer gets ffmpeg's turn too.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The destination was built from Jellyfin's own resolved video path, nothing from the request.")]
    private async Task<bool> TryWriteNativeAsync(Stream source, string destination, string extension, CancellationToken cancellationToken)
    {
        var temp = ExtractedSubtitleNames.TempPathFor(destination);

        try
        {
            await using (source.ConfigureAwait(false))
            {
                var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

                await using (file.ConfigureAwait(false))
                {
                    await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!HasContent(temp, extension))
            {
                _logger.LogDebug("Jellyfin's extractor handed back an empty subtitle for {Destination}, trying ffmpeg", destination);
                return false;
            }

            using (await SubtitleFileLock.AcquireAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                File.Move(temp, destination, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't write what Jellyfin's extractor handed back to {Destination}, trying ffmpeg", destination);
            return false;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Turns a picked subtitle option into a real file on disk. An external one already
    /// is one; a track still inside the video is pulled out next to it first. From then on
    /// it is an ordinary sidecar, so this only ever does the extraction once per track.
    /// </summary>
    /// <param name="item">The item the subtitle belongs to.</param>
    /// <param name="option">The subtitle that was picked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file path, or a reason it couldn't be produced.</returns>
    public async Task<(string? Path, string? Error)> ResolveAsync(
        BaseItem item,
        SubtitleOption option,
        CancellationToken cancellationToken = default)
    {
        if (!option.IsEmbedded)
        {
            return (option.Path, null);
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return (null, "That item has no video file to extract from.");
        }

        if (!TryParseKey(option.Path, out var streamIndex))
        {
            return (null, "That subtitle track couldn't be identified.");
        }

        try
        {
            var extracted = await ExtractAsync(item, streamIndex, option.Codec, option.Language, cancellationToken)
                .ConfigureAwait(false);

            return (extracted, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return (null, ex.Message);
        }
    }

    private bool HasFfmpeg()
    {
        var path = _mediaEncoder.EncoderPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever removes a temporary file this class just named.")]
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
            // a leftover temporary file is untidy, not a failure worth reporting over the
            // one that actually went wrong
        }
    }
}
