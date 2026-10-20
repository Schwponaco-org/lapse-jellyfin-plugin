// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Scheduled extraction (experimental): goes through the picked libraries and writes the
/// embedded tracks matching the settings out as files next to each video.
///
/// Jellyfin can extract embedded subs already, but it grabs every track into its cache.
/// Someone who only wants forced English shouldn't pay for twenty other tracks per file.
///
/// Second runs are cheap: extracted files have fixed names so they're spotted and skipped,
/// usually without opening the video. Jellyfin's stream record decides whether a file is
/// worth looking at, the file itself is only read when there's something to extract or
/// the record can't be trusted (file changed since the scan, or the library doesn't keep
/// embedded subtitle info).
/// </summary>
public class EmbeddedSubtitleExtractionService
{
    private const int MaxFailuresKept = 20;
    private const int MaxCacheEntries = 50_000;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly LibraryService _libraryService;
    private readonly SubtitleLanguages _languages;
    private readonly EmbeddedSubtitleProbe _probe;
    private readonly SubtitleExtractor _extractor;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<EmbeddedSubtitleExtractionService> _logger;

    // ffprobe answers per file, kept while size and write time stay the same. Saves
    // re-reading every file each night in libraries whose DB record can't be trusted.
    private readonly ConcurrentDictionary<string, (long Size, DateTime WriteTimeUtc, List<EmbeddedSubtitleTrack> Tracks)> _probeCache = new(StringComparer.Ordinal);

    // tracks that came out empty (placeholder forced tracks), so we don't read them every run
    private readonly ConcurrentDictionary<string, byte> _knownEmpty = new(StringComparer.Ordinal);

    private volatile ExtractionRunSummary? _lastRun;

    // 1 while running. The scheduler won't start it twice but the dashboard button can try.
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSubtitleExtractionService"/> class.
    /// </summary>
    public EmbeddedSubtitleExtractionService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        LibraryService libraryService,
        SubtitleLanguages languages,
        EmbeddedSubtitleProbe probe,
        SubtitleExtractor extractor,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<EmbeddedSubtitleExtractionService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _libraryService = libraryService;
        _languages = languages;
        _probe = probe;
        _extractor = extractor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets what the last run did, null if none since the server started.
    /// </summary>
    public ExtractionRunSummary? LastRun => _lastRun;

    /// <summary>
    /// Gets a value indicating whether a run is going.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Puts a path or path filter in one form for comparing: composed Unicode (macOS and some
    /// SMB shares give decomposed names, so a typed "Amélie" wouldn't match) and forward slashes.
    /// </summary>
    public static string? NormalizeForMatch(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Normalize(NormalizationForm.FormC).Replace('\\', '/');
    }

    /// <summary>
    /// Says whether a video path contains the path filter, ignoring case.
    /// </summary>
    public static bool PassesPathFilter(string videoPath, string? normalizedFilter)
    {
        return normalizedFilter is null
            || NormalizeForMatch(videoPath)!.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Goes through the picked libraries once.
    /// </summary>
    public async Task<ExtractionRunSummary> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var summary = new ExtractionRunSummary { StartedUtc = DateTime.UtcNow };

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            summary.Message = "An extraction is already running.";
            summary.FinishedUtc = DateTime.UtcNow;
            return summary;
        }

        try
        {
            _lastRun = summary;
            await RunLockedAsync(summary, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
            throw;
        }
        finally
        {
            summary.FinishedUtc = DateTime.UtcNow;
            progress.Report(100);
            Volatile.Write(ref _running, 0);

            _logger.LogInformation(
                "Subtitle extraction {State}: {Videos} videos looked at ({Probed} read directly), {Written} subtitles written, " +
                "{Already} already there, {Covered} covered by an existing subtitle, {Empty} empty, {Unsupported} not extractable, {Failed} failed{Message}",
                summary.Cancelled ? "stopped" : "finished",
                summary.Videos,
                summary.Probed,
                summary.Written,
                summary.AlreadyExtracted,
                summary.CoveredByExisting,
                summary.Empty,
                summary.Unsupported,
                summary.Failed,
                summary.Message is null ? string.Empty : " (" + summary.Message + ")");
        }

        return summary;
    }

    private async Task RunLockedAsync(ExtractionRunSummary summary, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.ExtractEmbeddedEnabled)
        {
            summary.Message = "Extraction is turned off in LAPSE's Experimental settings.";
            return;
        }

        if (!_probe.IsAvailable)
        {
            summary.Message = "The server hasn't told the plugin where its ffprobe is, and extraction needs it to read the files.";
            return;
        }

        var existingLibraries = _libraryService.GetLibraryIdSet();
        var libraries = config.ExtractLibraryIds.Where(existingLibraries.Contains).Distinct().ToList();
        if (libraries.Count == 0)
        {
            summary.Message = "No library is picked for extraction.";
            return;
        }

        var settings = new RunSettings(
            SubtitleLanguages.SplitList(config.ExtractLanguages).Select(_languages.Resolve).ToList(),
            config.ExtractTrackFilter,
            config.ExtractSkipExisting,
            config.ExtractPictureSubtitles,
            NormalizeForMatch(config.ExtractPathFilter));

        // library options are the same for everything in a library, so look them up once
        var work = new List<(BaseItem Item, LibraryOptions? Options)>();
        var seenItems = new HashSet<Guid>();

        foreach (var libraryId in libraries)
        {
            var options = LibraryOptionsFor(libraryId);

            foreach (var (item, _) in _libraryService.GetItemsWithLibrary(libraryId))
            {
                if (seenItems.Add(item.Id))
                {
                    work.Add((item, options));
                }
            }
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < work.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / work.Count);

            var (item, options) = work[i];

            foreach (var video in VideosOf(item))
            {
                if (!seenPaths.Add(video.Path) || !PassesPathFilter(video.Path, settings.PathFilter))
                {
                    continue;
                }

                try
                {
                    await ProcessVideoAsync(video, options, settings, summary, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // one bad file is that file's problem, not the run's
                    summary.Failed++;
                    AddFailure(summary, video.Path, ex.Message);
                    _logger.LogWarning(ex, "Could not extract subtitles from {Video}", video.Path);
                }
            }
        }

        if (summary.Videos == 0)
        {
            summary.Message = settings.PathFilter is null
                ? "The picked libraries have no video files in them."
                : "No video's path contains \"" + config.ExtractPathFilter + "\".";
        }
    }

    private LibraryOptions? LibraryOptionsFor(Guid libraryId)
    {
        try
        {
            return _libraryManager.GetItemById(libraryId) is { } folder ? _libraryManager.GetLibraryOptions(folder) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            _logger.LogDebug(ex, "Could not read the library settings for {Library}", libraryId);
            return null;
        }
    }

    // The item plus any other versions next to it ("Film - 720p.mkv" / "Film - 1080p.mkv").
    // 10.11 keeps those as paths on the item and 12 links them, both fill in the media
    // sources, so check both.
    private List<BaseItem> VideosOf(BaseItem item)
    {
        var result = new List<BaseItem> { item };

        if (item is not Video video)
        {
            return result;
        }

        // path -> item id where known. Only media sources carry the id, and on 12 that's the
        // only way to look a version up.
        var versions = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var path in video.LocalAlternateVersions ?? Array.Empty<string>())
        {
            if (!string.IsNullOrEmpty(path))
            {
                versions.TryAdd(path, null);
            }
        }

        try
        {
            foreach (var source in _mediaSourceManager.GetStaticMediaSources(video, false))
            {
                if (source.Protocol == MediaProtocol.File && !string.IsNullOrEmpty(source.Path))
                {
                    versions[source.Path] = source.Id;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not list the versions of {Video}", item.Path);
        }

        foreach (var (path, id) in versions)
        {
            if (string.Equals(path, item.Path, StringComparison.Ordinal))
            {
                continue;
            }

            BaseItem? alternate = null;

            try
            {
                if (Guid.TryParse(id, out var itemId))
                {
                    alternate = _libraryManager.GetItemById(itemId);
                }

                if (alternate is null || !string.Equals(alternate.Path, path, StringComparison.Ordinal))
                {
                    alternate = _libraryManager.FindByPath(path, false);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Could not look up the alternate version at {Path}", path);
            }

            if (alternate is not null && string.Equals(alternate.Path, path, StringComparison.Ordinal))
            {
                result.Add(alternate);
            }
            else
            {
                _logger.LogDebug("Found another version of {Video} at {Path}, but not the item for it", item.Path, path);
            }
        }

        return result;
    }

    private async Task ProcessVideoAsync(BaseItem item, LibraryOptions? options, RunSettings settings, ExtractionRunSummary summary, CancellationToken cancellationToken)
    {
        if (!IsPlainVideoFile(item))
        {
            return;
        }

        var videoPath = item.Path;
        var file = new FileInfo(videoPath);
        if (!file.Exists)
        {
            return;
        }

        summary.Videos++;

        // Jellyfin's record tells us for free whether there's anything worth extracting. For
        // most files there isn't and they never get opened. Its stream numbers aren't the
        // file's though, so if there IS something we read the file (once per change).
        var streams = item.GetMediaStreams();
        var known = TracksFromDatabase(item, streams, file, options, settings);
        if (known is not null && !known.Exists(t => IsWanted(t, settings)))
        {
            return;
        }

        var probed = await ProbeAsync(videoPath, file, summary, cancellationToken).ConfigureAwait(false);
        if (probed is null)
        {
            summary.Failed++;
            AddFailure(summary, videoPath, "ffprobe couldn't read the file.");
            return;
        }

        var jobs = PlanJobs(item, streams, file, probed, settings, summary);
        if (jobs.Count == 0)
        {
            return;
        }

        var results = await _extractor.ExtractTracksAsync(videoPath, jobs, settings.IncludePictures, cancellationToken).ConfigureAwait(false);
        var wroteAny = false;

        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case TrackExtractionOutcome.Written:
                    summary.Written++;
                    wroteAny = true;
                    break;
                case TrackExtractionOutcome.LeftAlone:
                    summary.AlreadyExtracted++;
                    break;
                case TrackExtractionOutcome.Empty:
                    summary.Empty++;
                    _knownEmpty.TryAdd(EmptyKey(file, result.Job.Track.Index), 0);
                    break;
                default:
                    summary.Failed++;
                    AddFailure(summary, videoPath, $"track {result.Job.Track.Index}: {result.Error}");
                    break;
            }
        }

        if (wroteAny)
        {
            RequestRefresh(item);
        }
    }

    // The tracks Jellyfin has on record, or null if that record can't be trusted to say
    // "nothing here". We only look at what the tracks are, never their numbers.
    private static List<EmbeddedSubtitleTrack>? TracksFromDatabase(
        BaseItem item,
        IReadOnlyList<MediaStream> streams,
        FileInfo file,
        LibraryOptions? options,
        RunSettings settings)
    {
        // libraries set to not keep embedded subs (or text only) leave them out of the record
        var keeps = options?.AllowEmbeddedSubtitles ?? EmbeddedSubtitleOptions.AllowAll;
        var keepsText = keeps is EmbeddedSubtitleOptions.AllowAll or EmbeddedSubtitleOptions.AllowText;
        var keepsPictures = keeps is EmbeddedSubtitleOptions.AllowAll or EmbeddedSubtitleOptions.AllowImage;

        if (!keepsText || (settings.IncludePictures && !keepsPictures))
        {
            return null;
        }

        // changed since Jellyfin read it, may have different tracks now
        if (Math.Abs((file.LastWriteTimeUtc - DateTime.SpecifyKind(item.DateModified, DateTimeKind.Utc)).TotalSeconds) > 2)
        {
            return null;
        }

        return streams
            .Where(s => s.Type == MediaStreamType.Subtitle && string.IsNullOrEmpty(s.Path) && !s.IsExternal)
            .Select(s => new EmbeddedSubtitleTrack
            {
                Index = s.Index,
                Codec = s.Codec,
                Language = s.Language,
                Title = s.Title,
                ForcedFlag = s.IsForced,
                HearingImpairedFlag = s.IsHearingImpaired
            })
            .ToList();
    }

    // Picks the tracks to write and counts the ones it skips into the summary.
    private List<TrackExtractionJob> PlanJobs(
        BaseItem item,
        IReadOnlyList<MediaStream> streams,
        FileInfo file,
        List<EmbeddedSubtitleTrack> tracks,
        RunSettings settings,
        ExtractionRunSummary summary)
    {
        var jobs = new List<TrackExtractionJob>();
        List<(SubtitleLanguageKey Language, bool Forced)>? existing = null;

        foreach (var track in tracks)
        {
            if (!IsWanted(track, settings))
            {
                continue;
            }

            var extension = SubtitleExtractor.GetExtensionForCodec(track.Codec, settings.IncludePictures);
            if (extension is null)
            {
                summary.Unsupported++;
                continue;
            }

            if (_knownEmpty.ContainsKey(EmptyKey(file, track.Index)))
            {
                summary.Empty++;
                continue;
            }

            var destination = ExtractedSubtitleNames.BuildPath(file.FullName, track.Index, track.Language, track.IsForced, track.IsHearingImpaired, extension);

            if (SubtitleExtractor.IsCurrent(item, destination))
            {
                summary.AlreadyExtracted++;
                continue;
            }

            if (settings.SkipExisting)
            {
                existing ??= ExistingSubtitles(streams, file.FullName);
                var language = _languages.Resolve(track.Language);

                if (existing.Exists(e => e.Forced == track.IsForced && SubtitleLanguages.SameLanguage(e.Language, language)))
                {
                    summary.CoveredByExisting++;
                    continue;
                }
            }

            if (!ExtractedSubtitleNames.FitsFileSystem(destination))
            {
                summary.Failed++;
                AddFailure(summary, file.FullName, $"track {track.Index}: the video's name is too long to add the language and track number to");
                continue;
            }

            // an out of date copy from before the video was replaced gets written over (if nothing touches it meanwhile)
            var writeTime = File.Exists(destination) ? File.GetLastWriteTimeUtc(destination) : (DateTime?)null;
            jobs.Add(new TrackExtractionJob(track, destination, ReplaceExisting: writeTime is not null, ExistingWriteTimeUtc: writeTime));
        }

        return jobs;
    }

    private bool IsWanted(EmbeddedSubtitleTrack track, RunSettings settings)
    {
        if ((settings.Filter == ExtractTrackFilter.ForcedOnly && !track.IsForced)
            || (settings.Filter == ExtractTrackFilter.NotForced && track.IsForced))
        {
            return false;
        }

        return settings.Languages.Count == 0 || SubtitleLanguages.Matches(settings.Languages, _languages.Resolve(track.Language));
    }

    // Subtitle files Jellyfin shows (or will after its next scan) for this video, with their
    // language and forced flag read the way Jellyfin reads them. No language = covers nothing.
    private List<(SubtitleLanguageKey Language, bool Forced)> ExistingSubtitles(IReadOnlyList<MediaStream> streams, string videoPath)
    {
        var result = new List<(SubtitleLanguageKey, bool)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stream in streams)
        {
            if (stream.Type != MediaStreamType.Subtitle || string.IsNullOrEmpty(stream.Path) || !File.Exists(stream.Path))
            {
                continue;
            }

            seen.Add(stream.Path);

            var (nameLanguage, nameForced) = ExistingSubtitleNames.Parse(videoPath, stream.Path, _languages.IsLanguage);
            var language = stream.Language ?? nameLanguage;

            if (!string.IsNullOrWhiteSpace(language))
            {
                result.Add((_languages.Resolve(language), stream.IsForced || nameForced));
            }
        }

        var folder = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(folder))
        {
            return result;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var path in files)
        {
            if (seen.Contains(path)
                || !SubtitleFormats.IsSubtitle(path)
                || Path.GetFileName(path).Contains(".lapse-", StringComparison.OrdinalIgnoreCase)
                || !ExistingSubtitleNames.IsNamedAfter(videoPath, path))
            {
                continue;
            }

            var (language, forced) = ExistingSubtitleNames.Parse(videoPath, path, _languages.IsLanguage);
            if (language is not null)
            {
                result.Add((_languages.Resolve(language), forced));
            }
        }

        return result;
    }

    private async Task<List<EmbeddedSubtitleTrack>?> ProbeAsync(string videoPath, FileInfo file, ExtractionRunSummary summary, CancellationToken cancellationToken)
    {
        if (_probeCache.TryGetValue(videoPath, out var cached)
            && cached.Size == file.Length
            && cached.WriteTimeUtc == file.LastWriteTimeUtc)
        {
            return cached.Tracks;
        }

        summary.Probed++;
        var tracks = await _probe.ProbeAsync(videoPath, cancellationToken).ConfigureAwait(false);

        if (tracks is not null)
        {
            if (_probeCache.Count >= MaxCacheEntries)
            {
                _probeCache.Clear();
            }

            _probeCache[videoPath] = (file.Length, file.LastWriteTimeUtc, tracks);
        }

        return tracks;
    }

    // a plain file ffmpeg can open. disc folders, ISOs, .strm and URLs are left alone
    private static bool IsPlainVideoFile(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path) || item.LocationType == LocationType.Virtual || !item.IsFileProtocol)
        {
            return false;
        }

        return item is not Video video || (video.VideoType == VideoType.VideoFile && !video.IsShortcut);
    }

    private void RequestRefresh(BaseItem item)
    {
        try
        {
            _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Could not ask Jellyfin to rescan {Item} for its new subtitle files", item.Path);
        }
    }

    private static string EmptyKey(FileInfo file, int streamIndex)
    {
        return string.Join('|', file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, streamIndex);
    }

    private static void AddFailure(ExtractionRunSummary summary, string videoPath, string? reason)
    {
        var failure = Path.GetFileName(videoPath) + ": " + (reason ?? "unknown error");

        // the dashboard can read this while the run adds to it
        lock (summary.Failures)
        {
            if (summary.Failures.Count < MaxFailuresKept)
            {
                summary.Failures.Add(failure);
            }
        }
    }

    private sealed record RunSettings(
        List<SubtitleLanguageKey> Languages,
        ExtractTrackFilter Filter,
        bool SkipExisting,
        bool IncludePictures,
        string? PathFilter);
}
