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
/// The scheduled extraction: goes through the picked libraries and writes the embedded
/// subtitle tracks that match the settings out as files beside each video. Experimental.
///
/// The point is choosing. Jellyfin can already extract embedded subtitles, but it takes
/// every one in every file and puts them in its cache, and someone who only ever wants
/// the forced English track is paying for twenty others per file. This takes only the
/// languages and kinds asked for, writes them where Jellyfin shows them as ordinary
/// external subtitles, and leaves everything else in the video untouched.
///
/// A second run over the same library does next to nothing: each track has a fixed file
/// name, so one already written is recognised and passed over, usually without opening
/// the video at all. Jellyfin's own record of a file's streams decides whether it's worth
/// looking at; the file itself is only read when there's something to extract, or when
/// that record can't be trusted - the file changed since it was scanned, or the library
/// is set to not keep embedded subtitles, which leaves the record empty.
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

    // What ffprobe said about a file, kept for as long as the file stays the same size
    // and age. Only the libraries whose database can't be trusted get probed every run,
    // and this keeps those from reading every file again every night.
    private readonly ConcurrentDictionary<string, (long Size, DateTime WriteTimeUtc, List<EmbeddedSubtitleTrack> Tracks)> _probeCache = new(StringComparer.Ordinal);

    // Tracks that came out empty, so a placeholder forced track isn't read through again
    // on every run for as long as the server is up.
    private readonly ConcurrentDictionary<string, byte> _knownEmpty = new(StringComparer.Ordinal);

    private volatile ExtractionRunSummary? _lastRun;

    // 1 while a run is going. The scheduler never starts the task twice, but the
    // dashboard's run button goes through the same door.
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSubtitleExtractionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Looks up items and library settings.</param>
    /// <param name="mediaSourceManager">Lists the versions of a film.</param>
    /// <param name="libraryService">Walks a library's items.</param>
    /// <param name="languages">Compares language tags.</param>
    /// <param name="probe">Reads a file's subtitle streams.</param>
    /// <param name="extractor">Writes the tracks out.</param>
    /// <param name="providerManager">Asks Jellyfin to rescan items that got new files.</param>
    /// <param name="fileSystem">Needed for that rescan.</param>
    /// <param name="logger">Logger.</param>
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
    /// Gets what the last run did, or null if there hasn't been one since the server
    /// started.
    /// </summary>
    public ExtractionRunSummary? LastRun => _lastRun;

    /// <summary>
    /// Gets a value indicating whether a run is going now.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Goes through the picked libraries once.
    /// </summary>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
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
                summary.Message is null ? string.Empty : " - " + summary.Message);
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

        var items = new List<BaseItem>();
        var seenItems = new HashSet<Guid>();

        foreach (var libraryId in libraries)
        {
            foreach (var (item, _) in _libraryService.GetItemsWithLibrary(libraryId))
            {
                if (seenItems.Add(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / items.Count);

            foreach (var video in VideosOf(items[i]))
            {
                if (!seenPaths.Add(video.Path) || !PassesPathFilter(video.Path, settings.PathFilter))
                {
                    continue;
                }

                try
                {
                    await ProcessVideoAsync(video, settings, summary, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // One unreadable file is that file's problem, not the run's.
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

    // The item itself, and any other versions of it sitting beside it ("Film - 720p.mkv"
    // next to "Film - 1080p.mkv"). Jellyfin 10.11 lists those as paths on the item and
    // Jellyfin 12 links them instead, so they're found through the media sources the
    // server hands a player, which both versions fill in, with the path list as well.
    private List<BaseItem> VideosOf(BaseItem item)
    {
        var result = new List<BaseItem> { item };

        if (item is not Video video)
        {
            return result;
        }

        // Path to item id, where the id is known. The same version can turn up in both
        // lists, and only the media source carries its id, which on Jellyfin 12 is the only
        // way to look it up.
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Every path here is Jellyfin's own resolved path for a library item, or built from one by ExtractedSubtitleNames.")]
    private async Task ProcessVideoAsync(BaseItem item, RunSettings settings, ExtractionRunSummary summary, CancellationToken cancellationToken)
    {
        var videoPath = item.Path;

        if (!IsPlainVideoFile(item))
        {
            return;
        }

        var file = new FileInfo(videoPath);
        if (!file.Exists)
        {
            return;
        }

        summary.Videos++;

        // Jellyfin's own record answers one question for free: does this file have any
        // track worth extracting at all? For most of a library it doesn't, and those
        // files are never opened. It can't answer anything past that - its stream numbers
        // aren't the file's once there's a subtitle beside the video - so for every file
        // that does, the file itself is read (once per change, the answer is kept).
        var known = TracksFromDatabase(item, file, settings);
        if (known is not null && !known.Any(t => IsWanted(t, settings)))
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

        var jobs = PlanJobs(item, file, probed, settings, summary);
        if (jobs.Count == 0)
        {
            return;
        }

        var results = await _extractor
            .ExtractTracksAsync(videoPath, jobs, settings.IncludePictures, cancellationToken)
            .ConfigureAwait(false);

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

    // The tracks Jellyfin has on record, or null when that record can't be relied on to
    // say there's nothing to do. Only what they are is used, never their numbers.
    private List<EmbeddedSubtitleTrack>? TracksFromDatabase(BaseItem item, FileInfo file, RunSettings settings)
    {
        // A library set to not keep embedded subtitles drops them from its record
        // entirely, text only drops the pictures. Either way an empty answer there
        // doesn't mean the file has none.
        LibraryOptions? options = null;
        try
        {
            options = _libraryManager.GetLibraryOptions(item);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            _logger.LogDebug(ex, "Could not read the library settings for {Video}", item.Path);
        }

        var keeps = options?.AllowEmbeddedSubtitles ?? EmbeddedSubtitleOptions.AllowAll;
        var keepsText = keeps is EmbeddedSubtitleOptions.AllowAll or EmbeddedSubtitleOptions.AllowText;
        var keepsPictures = keeps is EmbeddedSubtitleOptions.AllowAll or EmbeddedSubtitleOptions.AllowImage;

        if (!keepsText || (settings.IncludePictures && !keepsPictures))
        {
            return null;
        }

        // A file changed since Jellyfin last read it may have different tracks now.
        if (Math.Abs((file.LastWriteTimeUtc - DateTime.SpecifyKind(item.DateModified, DateTimeKind.Utc)).TotalSeconds) > 2)
        {
            return null;
        }

        return item.GetMediaStreams()
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

    // Works out which of a file's tracks to write, counting the ones it passes over into
    // the summary.
    private List<TrackExtractionJob> PlanJobs(
        BaseItem item,
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

            var destination = ExtractedSubtitleNames.BuildPath(
                file.FullName, track.Index, track.Language, track.IsForced, track.IsHearingImpaired, extension);

            if (SubtitleExtractor.IsCurrent(item, destination))
            {
                summary.AlreadyExtracted++;
                continue;
            }

            if (settings.SkipExisting)
            {
                existing ??= ExistingSubtitles(item, file.FullName);
                var language = _languages.Resolve(track.Language);

                if (existing.Any(e => e.Forced == track.IsForced && SubtitleLanguages.SameLanguage(e.Language, language)))
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

            // An out of date copy from before the video was replaced is written over, as
            // long as nothing touches it in the meantime.
            var writeTime = File.Exists(destination) ? File.GetLastWriteTimeUtc(destination) : (DateTime?)null;
            jobs.Add(new TrackExtractionJob(track, destination, ReplaceExisting: writeTime is not null, ExistingWriteTimeUtc: writeTime));
        }

        return jobs;
    }

    private bool IsWanted(EmbeddedSubtitleTrack track, RunSettings settings)
    {
        switch (settings.Filter)
        {
            case ExtractTrackFilter.ForcedOnly when !track.IsForced:
            case ExtractTrackFilter.NotForced when track.IsForced:
                return false;
        }

        return settings.Languages.Count == 0 || SubtitleLanguages.Matches(settings.Languages, _languages.Resolve(track.Language));
    }

    // The subtitle files Jellyfin shows, or will show after its next scan, for this video:
    // the external ones it has on record, and files beside the video named after it.
    // Each comes with its language and whether it's forced, read the way Jellyfin reads
    // them. A file whose language can't be told doesn't cover anything.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Lists the folder of a video Jellyfin already resolved.")]
    private List<(SubtitleLanguageKey Language, bool Forced)> ExistingSubtitles(BaseItem item, string videoPath)
    {
        var result = new List<(SubtitleLanguageKey, bool)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stream in item.GetMediaStreams())
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

    // A video file on disk that ffmpeg can open as one. Disc folders, ISOs, .strm links
    // and anything streamed from a URL are left alone.
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
            _providerManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                RefreshPriority.Normal);
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

    /// <summary>
    /// Puts a path or a path filter into one form for comparing: composed Unicode (macOS
    /// and some SMB shares hand back decomposed names, so "Amélie" typed in a box wouldn't
    /// otherwise match "Amélie" on disk) and forward slashes.
    /// </summary>
    /// <param name="value">The path or filter.</param>
    /// <returns>The normalised form, or null for a blank one.</returns>
    public static string? NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Normalize(NormalizationForm.FormC).Replace('\\', '/');
    }

    /// <summary>
    /// Says whether a video path contains the path filter, ignoring case.
    /// </summary>
    /// <param name="videoPath">The video's path.</param>
    /// <param name="normalizedFilter">The filter from <see cref="NormalizeForMatch"/>, or null for none.</param>
    /// <returns>True when there's no filter or the path contains it.</returns>
    public static bool PassesPathFilter(string videoPath, string? normalizedFilter)
    {
        return normalizedFilter is null
            || NormalizeForMatch(videoPath)!.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddFailure(ExtractionRunSummary summary, string videoPath, string? reason)
    {
        AddFailure(summary, Path.GetFileName(videoPath) + ": " + (reason ?? "unknown error"));
    }

    // The dashboard can read the summary while a run is still adding to it, so the list
    // is only touched under its own lock.
    private static void AddFailure(ExtractionRunSummary summary, string failure)
    {
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
