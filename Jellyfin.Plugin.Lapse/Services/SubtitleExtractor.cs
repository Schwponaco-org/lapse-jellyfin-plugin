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
    /// The track was empty (some releases have a placeholder forced track), nothing to write.
    /// </summary>
    Empty,

    /// <summary>
    /// ffmpeg couldn't get it out.
    /// </summary>
    Failed,

    /// <summary>
    /// Something else wrote the file while we were running, so we left it alone.
    /// </summary>
    LeftAlone
}

/// <summary>
/// One track to pull out of a video, and where it goes.
/// </summary>
public sealed record TrackExtractionJob(
    EmbeddedSubtitleTrack Track,
    string Destination,
    bool ReplaceExisting = false,
    DateTime? ExistingWriteTimeUtc = null);

/// <summary>
/// What happened to one <see cref="TrackExtractionJob"/>.
/// </summary>
public sealed record TrackExtractionResult(TrackExtractionJob Job, TrackExtractionOutcome Outcome, string? Error = null);

/// <summary>
/// Pulls a subtitle track out of the video and writes it next to it as a normal file.
///
/// Engines can't read a track that only exists inside a container, and plenty of films
/// only have embedded subs. Once it's a file everything else works on it and Jellyfin
/// picks it up as another track. Only text tracks can come out for syncing, PGS/VobSub
/// are pictures (scheduled extraction can copy PGS out as .sup for Jellyfin to show).
///
/// A single track asked for by hand goes through Jellyfin's own subtitle encoder first,
/// it caches, so a track that's been played before is usually free. ffmpeg is the fallback
/// and the only thing scheduled extraction uses, since Jellyfin's encoder pulls out every
/// track at once and one empty track fails the lot.
///
/// The file is always asked which track sits at an index first. Jellyfin's DB describes the
/// file as it was at the last scan and a replaced file can have anything there.
/// </summary>
public class SubtitleExtractor
{
    /// <summary>
    /// Marks a subtitle option as still inside the video. The number after it is the stream index.
    /// </summary>
    public const string EmbeddedPrefix = "embedded://";

    private const string CouldNotExtract = "ffmpeg couldn't get that subtitle track out of the video file.";

    // what each text codec lands on disk as
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

    // Picture codecs we can copy out whole. Only PGS, VobSub needs an .idx too and DVB has no file format.
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
    /// Gets whether a subtitle "path" is really an embedded track key.
    /// </summary>
    public static bool IsEmbedded(string? path)
    {
        return path is not null && path.StartsWith(EmbeddedPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the key used instead of a path for an embedded track.
    /// </summary>
    public static string BuildKey(int streamIndex)
    {
        return EmbeddedPrefix + streamIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the stream index back out of an embedded track key.
    /// </summary>
    public static bool TryParseKey(string? path, out int streamIndex)
    {
        streamIndex = -1;

        return IsEmbedded(path)
            && int.TryParse(path![EmbeddedPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out streamIndex);
    }

    /// <summary>
    /// Gets the file extension for a text subtitle codec, null for picture codecs.
    /// </summary>
    public static string? GetExtensionForCodec(string? codec)
    {
        return codec is not null && TextCodecs.TryGetValue(codec, out var extension) ? extension : null;
    }

    /// <summary>
    /// Same, but optionally allowing picture codecs that can be copied out whole.
    /// </summary>
    public static string? GetExtensionForCodec(string? codec, bool allowPictures)
    {
        if (GetExtensionForCodec(codec) is { } text)
        {
            return text;
        }

        return allowPictures && codec is not null && PictureCodecs.TryGetValue(codec, out var picture) ? picture : null;
    }

    /// <summary>
    /// Says why a track can't be extracted, or null when it can.
    /// </summary>
    public string? GetExtractionProblem(string? codec)
    {
        return GetExtensionForCodec(codec) is null
            ? $"{(codec ?? "That").ToUpperInvariant()} subtitles are pictures of text rather than text, so there's nothing to extract or line up. They need OCR first, with something like Subtitle Edit."
            : null;
    }

    /// <summary>
    /// Says whether a subtitle file is an extracted copy of an embedded track that nobody
    /// has synced since. Background runs leave embedded tracks alone, and an untouched copy
    /// is the same track somewhere else. Once someone syncs it by hand it's a normal subtitle.
    /// </summary>
    public static bool IsUntouchedExtraction(BaseItem item, string? subtitlePath)
    {
        return ExtractedSubtitleNames.IsExtractedTrack(item.Path, subtitlePath) && !HasSyncRecord(item.Id, subtitlePath!);
    }

    /// <summary>
    /// Same question, for callers that already have the item's record.
    /// </summary>
    public static bool IsUntouchedExtraction(BaseItem item, string? subtitlePath, MovieSyncRecord? record)
    {
        return ExtractedSubtitleNames.IsExtractedTrack(item.Path, subtitlePath)
            && (record is null || !record.SyncedSubtitles.Exists(s => string.Equals(s.Path, subtitlePath, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Says whether an extracted file has anything in it (a cue for text, a PGS segment for
    /// .sup). An empty track still comes out as a file, a 0 byte .srt or a header-only .ass.
    /// </summary>
    public static bool HasContent(string path, string extension)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (string.Equals(extension, ".sup", StringComparison.OrdinalIgnoreCase))
        {
            // every PGS segment starts with "PG"
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
    /// Copies one track out of a video into a file beside it. Asks Jellyfin first, ffmpeg if that fails.
    /// </summary>
    /// <exception cref="NotSupportedException">The track isn't text, or ffmpeg is missing.</exception>
    /// <exception cref="InvalidDataException">Neither route produced a subtitle.</exception>
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

        // Jellyfin's number isn't the file's, find it in the file. If that can't be told
        // for sure we stop rather than write some other track under this name.
        var (track, jellyfinAgrees, _) = await FindInFileAsync(item, streamIndex, cancellationToken).ConfigureAwait(false);
        track.Codec ??= codec;
        track.Language ??= language;

        var destination = ExtractedSubtitleNames.BuildPath(videoPath, track.Index, track.Language, track.IsForced, track.IsHearingImpaired, extension);

        if (!ExtractedSubtitleNames.FitsFileSystem(destination))
        {
            throw new IOException("The video's file name is too long to put a subtitle beside it with the language and track number added.");
        }

        // already pulled out earlier, reuse it
        if (IsCurrent(item, destination))
        {
            _logger.LogDebug("Subtitle stream {Index} of {Video} is already extracted at {Destination}, reusing it", track.Index, videoPath, destination);
            return destination;
        }

        // Jellyfin's extractor goes by its own index, so only when that index is still right
        if (jellyfinAgrees
            && await TryReadNativeAsync(item, streamIndex, extension, cancellationToken).ConfigureAwait(false) is { } native
            && await TryWriteNativeAsync(native, destination, extension, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Extracted subtitle stream {Index} from {Video} to {Destination} using Jellyfin's extractor", track.Index, videoPath, destination);
            return destination;
        }

        var job = new TrackExtractionJob(track, destination, ReplaceExisting: File.Exists(destination), ExistingWriteTimeUtc: WriteTimeOrNull(destination));
        var result = (await ExtractTracksAsync(videoPath, new[] { job }, allowPictures: false, cancellationToken).ConfigureAwait(false))[0];

        return result.Outcome switch
        {
            TrackExtractionOutcome.Written or TrackExtractionOutcome.LeftAlone => destination,
            TrackExtractionOutcome.Empty => throw new InvalidDataException("That subtitle track is empty. Some releases carry one as a placeholder with nothing in it."),
            _ => throw new InvalidDataException(result.Error ?? CouldNotExtract)
        };
    }

    /// <summary>
    /// Pulls several tracks out of one video in a single ffmpeg pass. One output ffmpeg
    /// can't write fails the whole run though, so on failure each track gets its own run.
    /// An empty track isn't a failure, ffmpeg writes an empty file and we throw it away.
    /// Everything goes to a temp name first so a run that dies halfway never leaves a
    /// file that looks finished.
    /// </summary>
    public async Task<IReadOnlyList<TrackExtractionResult>> ExtractTracksAsync(
        string videoPath,
        IReadOnlyList<TrackExtractionJob> jobs,
        bool allowPictures,
        CancellationToken cancellationToken)
    {
        var results = new TrackExtractionResult?[jobs.Count];
        var runnable = new List<int>();
        var hasFfmpeg = HasFfmpeg();

        for (var i = 0; i < jobs.Count; i++)
        {
            if (GetExtensionForCodec(jobs[i].Track.Codec, allowPictures) is null)
            {
                results[i] = new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "LAPSE can't write that kind of subtitle track out as a file.");
            }
            else if (!hasFfmpeg)
            {
                results[i] = new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, "The server hasn't told the plugin where its ffmpeg is.");
            }
            else
            {
                runnable.Add(i);
            }
        }

        if (runnable.Count > 0)
        {
            var combined = await RunAsync(videoPath, runnable.ConvertAll(i => jobs[i]), cancellationToken).ConfigureAwait(false);

            // Only worth splitting up a multi-track run. A timeout comes back as results,
            // not null, since reading the file once per track won't go any faster.
            if (combined is null && runnable.Count > 1)
            {
                _logger.LogInformation("Extracting {Count} subtitle tracks from {Video} in one go failed, trying them one at a time", runnable.Count, videoPath);

                foreach (var i in runnable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var single = await RunAsync(videoPath, new[] { jobs[i] }, cancellationToken).ConfigureAwait(false);
                    results[i] = single?[0] ?? new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, CouldNotExtract);
                }
            }
            else
            {
                for (var n = 0; n < runnable.Count; n++)
                {
                    var i = runnable[n];
                    results[i] = combined?[n] ?? new TrackExtractionResult(jobs[i], TrackExtractionOutcome.Failed, CouldNotExtract);
                }
            }
        }

        return results.Select(r => r!).ToList();
    }

    /// <summary>
    /// Finds one of Jellyfin's embedded streams in the actual file.
    ///
    /// Jellyfin numbers external subtitles first and the file's own streams after, so an
    /// item with two external subs has every embedded index two higher than ffmpeg's. Take
    /// that off, then the file has to agree on codec, language, title and forced flag. If it
    /// doesn't (file replaced since the scan) use the one stream that does agree, or none.
    /// </summary>
    /// <exception cref="InvalidDataException">The track's gone or there's more than one that could be it.</exception>
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

        var atIndex = probed.Find(t => t.Index == fileIndex);
        if (atIndex is not null && Agrees(atIndex, expected))
        {
            return (Merge(atIndex, expected), true, true);
        }

        var candidates = probed.FindAll(t => Agrees(t, expected));
        if (candidates.Count == 1)
        {
            _logger.LogWarning(
                "Subtitle stream {Expected} of {Video} isn't where Jellyfin has it, the file has changed since it was scanned. Using stream {Actual}, which matches it",
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
    /// Turns Jellyfin's index for an embedded stream into the file's own.
    /// </summary>
    public static int ToFileIndex(IEnumerable<MediaStream> streams, int jellyfinIndex)
    {
        return jellyfinIndex - streams.Count(s => IsExternalStream(s) && s.Index < jellyfinIndex);
    }

    /// <summary>
    /// Says whether an extracted file can be used as is: exists, not empty, and not left
    /// over from a video that's since been replaced. A file someone synced always counts,
    /// it's their work now.
    /// </summary>
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
    /// Says whether a file is older than the video it came from, i.e. the video got upgraded
    /// under the same name and this subtitle belongs to the old one.
    /// </summary>
    public static bool IsOlderThanVideo(string videoPath, FileInfo extracted)
    {
        try
        {
            var video = new FileInfo(videoPath);

            // 2s slack for coarse timestamps (FAT, some SMB shares)
            return video.Exists && extracted.LastWriteTimeUtc < video.LastWriteTimeUtc - TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns a picked subtitle into a file on disk. External ones already are, embedded
    /// ones get extracted next to the video (once, after that it's a normal sidecar).
    /// </summary>
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
            return (await ExtractAsync(item, streamIndex, option.Codec, option.Language, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return (null, ex.Message);
        }
    }

    // One ffmpeg run for all the given jobs. Null means ffmpeg itself failed and nothing it
    // wrote can be trusted, otherwise there's a result per job.
    private async Task<List<TrackExtractionResult>?> RunAsync(
        string videoPath,
        IReadOnlyList<TrackExtractionJob> jobs,
        CancellationToken cancellationToken)
    {
        var temps = jobs.Select(j => ExtractedSubtitleNames.TempPathFor(j.Destination)).ToList();

        // -copyts keeps the container's timestamps like Jellyfin and mkvextract do. Without
        // it ffmpeg shifts everything by the start time, a few tens of ms on B-frame video.
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

            // name the muxer, ffmpeg can't guess it from a temp file name
            arguments.AddRange(new[]
            {
                "-map", "0:" + jobs[i].Track.Index.ToString(CultureInfo.InvariantCulture),
                "-c:s", codecArgument,
                "-f", muxer,
                FfmpegPaths.AsFileUrl(temps[i])
            });
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
            catch (Exception ex) when (ex is TimeoutException or NotSupportedException)
            {
                if (ex is TimeoutException)
                {
                    _logger.LogWarning("ffmpeg timed out extracting subtitles from {Video}", videoPath);
                }

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
            temps.ForEach(TryDelete);
        }
    }

    // Moves a finished temp file into place if it has anything in it and the destination is still free.
    private async Task<TrackExtractionResult> PlaceAsync(string videoPath, TrackExtractionJob job, string temp, CancellationToken cancellationToken)
    {
        bool hasContent;
        try
        {
            hasContent = HasContent(temp, Path.GetExtension(job.Destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TrackExtractionResult(job, TrackExtractionOutcome.Failed, ex.Message);
        }

        if (!hasContent)
        {
            _logger.LogInformation("Subtitle stream {Index} of {Video} is empty, so there's nothing to write", job.Track.Index, videoPath);
            return new TrackExtractionResult(job, TrackExtractionOutcome.Empty);
        }

        // Locked so a sync or shift on the same file can't interleave. If the file showed
        // up or changed while ffmpeg ran, whoever wrote it wins.
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

        _logger.LogInformation("Extracted subtitle stream {Index} from {Video} to {Destination}", job.Track.Index, videoPath, job.Destination);
        return new TrackExtractionResult(job, TrackExtractionOutcome.Written);
    }

    // Tracks already in their output format are copied byte for byte (keeps ass styling).
    // mov_text and plain text have no muxer of their own, they become srt.
    private static (string Muxer, string CodecArgument) PlanOutput(string? codec)
    {
        return (codec ?? string.Empty).ToLowerInvariant() switch
        {
            "subrip" or "srt" => ("srt", "copy"),
            "ass" or "ssa" => ("ass", "copy"),
            "webvtt" or "vtt" => ("webvtt", "webvtt"),
            "hdmv_pgs_subtitle" or "pgssub" => ("sup", "copy"),
            _ => ("srt", "srt")
        };
    }

    private static bool IsExternalStream(MediaStream stream)
    {
        return stream.IsExternal || !string.IsNullOrEmpty(stream.Path);
    }

    // The file wins on everything it has. Jellyfin's title is kept if the file has none,
    // the forced/sdh guesses read it.
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

    // Codec, language and forced flag must match, plus the title if Jellyfin has one (that's
    // what tells two English tracks apart when a replaced file has them in a new order).
    private bool Agrees(EmbeddedSubtitleTrack probed, EmbeddedSubtitleTrack expected)
    {
        if (!string.Equals(CodecFamily(probed.Codec), CodecFamily(expected.Codec), StringComparison.Ordinal))
        {
            return false;
        }

        // a missing tag on either side doesn't contradict anything
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

    private static bool HasSyncRecord(Guid itemId, string path)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        lock (Plugin.ConfigurationLock)
        {
            return config.FindRecord(itemId)?.SyncedSubtitles.Exists(s => string.Equals(s.Path, path, StringComparison.Ordinal)) == true;
        }
    }

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

    // Jellyfin hands the track back as a stream (converted if its cached copy is another
    // format). Null means it couldn't and ffmpeg gets a go.
    private async Task<Stream?> TryReadNativeAsync(BaseItem item, int streamIndex, string extension, CancellationToken cancellationToken)
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
            _logger.LogDebug(ex, "Jellyfin's extractor couldn't hand over subtitle stream {Index} of {Item}, falling back to ffmpeg", streamIndex, item.Path);
            return null;
        }
    }

    // Writes what Jellyfin gave us through a temp file and only keeps it if there's a
    // subtitle in it. Empty means ffmpeg gets a go too.
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

    private bool HasFfmpeg()
    {
        var path = _mediaEncoder.EncoderPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
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
            // a stray temp file isn't worth reporting over whatever actually went wrong
        }
    }
}
