// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Runs bulk (and auto-sync) subtitle sync jobs one item at a time in the background,
/// so the dashboard doesn't have to wait around for a whole library to finish.
/// Bulk and auto-sync jobs always run the default engine's default mode against every
/// external subtitle an item has - there's no UI in the background path to pick
/// engine/mode/penalty/subtitle.
///
/// What it does to each of those subtitles is the Automation setting's business: sync
/// them, convert them, or both, and optionally translate the result. See
/// <see cref="AutomationAction"/>.
/// </summary>
public class SyncQueueManager : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly SubtitleConverter _converter;
    private readonly Translation.TranslationService _translationService;
    private readonly ILogger<SyncQueueManager> _logger;
    private readonly object _lock = new();
    private readonly List<QueueItem> _items = new();
    private readonly Queue<Guid> _pending = new();
    private readonly HashSet<Guid> _queuedIds = new();

    // Only one item is ever synced at a time. The worker loop is one caller; the
    // scheduled task's RunBatchAsync is another, and it runs items itself rather than
    // handing them to the worker so that Jellyfin's task runner has something to wait on.
    // Nothing stopped those two overlapping, which meant a per-library schedule firing
    // during the nightly run had both of them driving engines over the same library, and
    // potentially over the same file.
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Task? _worker;
    private string? _jobName;
    private string? _unitName;
    private string? _referenceKey;

    // Cancelled with Cancel(), replaced when the next job starts. Everything the queue
    // runs takes this token, so stopping a job stops the engine that is running right
    // now as well as everything still waiting in line.
    private CancellationTokenSource _jobCancellation = new();
    private bool _cancelRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueueManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up items.</param>
    /// <param name="libraryService">Works out which items are eligible.</param>
    /// <param name="subtitleLocator">Finds every subtitle for an item.</param>
    /// <param name="registry">Used to pick the configured default engine.</param>
    /// <param name="runner">Runs the engine.</param>
    /// <param name="converter">Changes a subtitle's format, for the automation actions
    /// that ask for it.</param>
    /// <param name="translationService">Translates subtitles, when automatic translation
    /// is turned on.</param>
    /// <param name="logger">Logger.</param>
    public SyncQueueManager(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SubtitleLocator subtitleLocator,
        EngineRegistry registry,
        EngineRunner runner,
        SubtitleConverter converter,
        Translation.TranslationService translationService,
        ILogger<SyncQueueManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _subtitleLocator = subtitleLocator;
        _registry = registry;
        _runner = runner;
        _converter = converter;
        _translationService = translationService;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether anything is queued or running right now.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count > 0 || _items.Any(i => i.Status == QueueItemStatus.Running);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an item or folder id is skipped (either directly,
    /// or because one of its ancestor folders is skipped).
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item should be left alone.</returns>
    public static bool IsSkipped(BaseItem item)
    {
        var skipped = Plugin.Instance?.Configuration.SkippedItemIds;
        if (skipped is null || skipped.Count == 0)
        {
            return false;
        }

        for (var current = item; current is not null; current = current.GetParent())
        {
            if (skipped.Contains(current.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a progress snapshot for the dashboard to poll.
    /// </summary>
    /// <returns>Current queue state.</returns>
    public QueueSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var current = _items.FirstOrDefault(i => i.Status == QueueItemStatus.Running);
            return new QueueSnapshot
            {
                Running = current is not null || _pending.Count > 0,
                Total = _items.Count,
                Completed = _items.Count(i => i.Status is QueueItemStatus.Done or QueueItemStatus.Failed or QueueItemStatus.Cancelled),
                Cancelling = _cancelRequested && current is not null,
                CurrentItemName = current?.Name,
                JobName = _jobName,
                UnitName = _unitName,
                Items = new List<QueueItem>(_items)
            };
        }
    }

    /// <summary>
    /// Starts a bulk sync job across every enabled library. Does nothing (and returns
    /// false) if a job is already running, since we only ever run one at a time.
    /// </summary>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueLibrary()
    {
        return StartBulkJob(_libraryService.GetItems());
    }

    /// <summary>
    /// Starts a bulk sync job for every syncable item under one library or folder.
    /// </summary>
    /// <param name="folderId">The library or folder to sync.</param>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueFolder(Guid folderId)
    {
        return StartBulkJob(_libraryService.GetItems(folderId));
    }

    /// <summary>
    /// Starts a bulk sync job over a specific set of items, which is how a whole series
    /// or season gets synced. Optionally lines every item's subtitles up against one of
    /// its own tracks instead of against the audio.
    /// </summary>
    /// <param name="items">The items to sync.</param>
    /// <param name="jobName">What to call this job in the progress readout.</param>
    /// <param name="unitName">What one item is, singular, e.g. "episode".</param>
    /// <param name="referenceKey">The reference track key from
    /// <see cref="SeriesSyncService.GetReferenceOptions"/>, or null to sync against the
    /// audio as usual.</param>
    /// <returns>True if a new job was started.</returns>
    public bool EnqueueItems(IReadOnlyList<BaseItem> items, string jobName, string unitName, string? referenceKey = null)
    {
        return StartBulkJob(items, jobName, unitName, referenceKey);
    }

    /// <summary>
    /// Adds one item to whatever's already queued, starting the worker if it isn't
    /// running. Used by auto-sync when something new shows up, and by the scheduled task.
    /// Skipped items are silently ignored.
    /// </summary>
    /// <param name="item">The item to sync.</param>
    public void EnqueueItem(BaseItem item)
    {
        // The ignore list is a standing "never touch this automatically", and everything
        // that reaches this method is automatic: auto-sync on a new file, the scheduled
        // task, the Radarr/Sonarr webhook. A hand press goes straight to the runner and
        // is deliberately still allowed.
        if (IsSkipped(item) || LibraryService.IsIgnored(item))
        {
            return;
        }

        lock (_lock)
        {
            if (_pending.Count == 0 && !_items.Any(i => i.Status == QueueItemStatus.Running))
            {
                // nothing is running, so this is the start of a fresh job - don't inherit
                // the reference track or the name of whatever ran last
                _referenceKey = null;
                _jobName = null;
                _unitName = "item";
                ResetCancellation();
            }

            if (!_queuedIds.Add(item.Id))
            {
                return;
            }

            _pending.Enqueue(item.Id);
            _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
        }

        EnsureWorkerRunning();
    }

    /// <summary>
    /// Stops the job that's running: drops everything still waiting and cancels the item
    /// being synced right now, which kills the engine process with it. Files already
    /// written stay written - this stops the run, it doesn't undo it.
    /// </summary>
    /// <returns>How many queued items were dropped, or null if nothing was running.</returns>
    public int? Cancel()
    {
        CancellationTokenSource? cancellation = null;
        int dropped;

        lock (_lock)
        {
            var running = _items.Any(i => i.Status == QueueItemStatus.Running);
            if (_pending.Count == 0 && !running)
            {
                return null;
            }

            dropped = _pending.Count;
            _pending.Clear();
            _queuedIds.Clear();
            _cancelRequested = true;

            foreach (var item in _items.Where(i => i.Status == QueueItemStatus.Queued))
            {
                item.Status = QueueItemStatus.Cancelled;
            }

            cancellation = _jobCancellation;
        }

        _logger.LogInformation("Sync job stopped by hand, {Dropped} queued items dropped", dropped);

        // Outside the lock: cancelling runs the continuations of whatever is waiting on
        // this token, and those want the lock themselves.
        cancellation.Cancel();
        return dropped;
    }

    /// <summary>
    /// Queues a batch of items and waits for all of them to finish, reporting progress as
    /// it goes. This is what the scheduled task uses, since Jellyfin's task runner wants
    /// a task that stays alive for as long as the work does.
    /// </summary>
    /// <param name="items">The items to sync.</param>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were synced without an error.</returns>
    public async Task<int> RunBatchAsync(
        IReadOnlyList<BaseItem> items,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var succeeded = 0;

        lock (_lock)
        {
            // a scheduled run is a fresh job, so don't carry the last one's rows along -
            // the dashboard's progress strip reads this list and a whole library's worth
            // of finished items would otherwise pile up until the server restarts
            if (_pending.Count == 0 && !_items.Any(i => i.Status == QueueItemStatus.Running))
            {
                _items.Clear();
                _queuedIds.Clear();
                _referenceKey = null;
                _jobName = "Scheduled sync";
                _unitName = "item";
                ResetCancellation();
            }
        }

        // A scheduled run gets the same Stop button as a bulk one, so it listens to both
        // Jellyfin's own token and the queue's.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, JobToken);
        cancellationToken = linked.Token;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            lock (_lock)
            {
                if (_items.All(existing => existing.ItemId != item.Id))
                {
                    _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
                }
            }

            if (await ProcessOneAsync(item.Id, cancellationToken).ConfigureAwait(false))
            {
                succeeded++;
            }

            progress?.Report((i + 1) * 100.0 / items.Count);
        }

        return succeeded;
    }

    /// <summary>
    /// Explains a result the plugin deliberately threw away, for the item list.
    /// </summary>
    /// <param name="result">The low-confidence result.</param>
    /// <returns>A line saying what happened and why.</returns>
    public static string DescribeSkip(SyncResult result)
    {
        if (result.AlreadyInSync)
        {
            var tolerance = Plugin.Instance?.Configuration.AlreadyInSyncToleranceMs ?? 100;
            return $"Already in sync (the engine would have moved it {result.OffsetMs}ms, which is inside the "
                + $"{tolerance}ms tolerance), so the file was left exactly as it was.";
        }

        var threshold = Plugin.Instance?.Configuration.ConfidenceSigma ?? LapseEngine.DefaultConfidenceSigma;

        // The engine's own words for what it thought of the answer. "nothing" means the
        // audio didn't back it up at all, which nearly always means the subtitle isn't for
        // this video; "unsure" means it's probably right but not by enough to overwrite.
        var verdict = result.Verdict switch
        {
            "nothing" => "the audio doesn't back that answer up",
            "unsure" => "it wasn't sure enough to touch the original",
            _ => "the engine wasn't confident"
        };

        var measured = result.Sigma.HasValue
            ? $" (scored {result.Sigma.Value:0.#} against a threshold of {threshold:0.#})"
            : string.Empty;

        // Anyone reading this has already decided the engine is being too careful, so the
        // way to overrule it goes in the message rather than being left to be found.
        return $"Left the original alone: {verdict}{measured}. To sync it anyway, turn on "
            + "\"Sync even when the engine is unsure\" under Settings, Engines, Advanced.";
    }

    /// <summary>
    /// Builds a name for the queue list that says enough to tell items apart. Episodes get
    /// their series and episode number, since "Episode 3" on its own is useless.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>A display name.</returns>
    public static string DescribeItem(BaseItem item)
    {
        var name = item.Name ?? "Unknown";

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var series = episode.SeriesName;
            var numbers = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? $"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} "
                : string.Empty;

            return string.IsNullOrEmpty(series) ? numbers + name : $"{series} - {numbers}{name}";
        }

        return name;
    }

    private bool StartBulkJob(
        IReadOnlyList<BaseItem> items,
        string? jobName = null,
        string? unitName = null,
        string? referenceKey = null)
    {
        lock (_lock)
        {
            if (_pending.Count > 0 || _items.Any(i => i.Status == QueueItemStatus.Running))
            {
                return false;
            }

            _items.Clear();
            _queuedIds.Clear();
            _jobName = jobName;
            _unitName = unitName ?? "item";
            _referenceKey = referenceKey;
            ResetCancellation();

            foreach (var item in items)
            {
                _queuedIds.Add(item.Id);
                _pending.Enqueue(item.Id);
                _items.Add(new QueueItem { ItemId = item.Id, Name = DescribeItem(item) });
            }
        }

        if (items.Count == 0)
        {
            return false;
        }

        EnsureWorkerRunning();
        return true;
    }

    private void EnsureWorkerRunning()
    {
        lock (_lock)
        {
            if (_worker is not null && !_worker.IsCompleted)
            {
                return;
            }

            _worker = Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            Guid itemId;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                itemId = _pending.Dequeue();
                _queuedIds.Remove(itemId);
            }

            await ProcessOneAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources this holds.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runGate.Dispose();
            _jobCancellation.Dispose();
        }
    }

    private async Task<bool> ProcessOneAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Whatever the caller handed us, plus the Stop button.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, JobToken);
        var token = linked.Token;

        try
        {
            await _runGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetItemStatus(itemId, QueueItemStatus.Cancelled);
            return false;
        }

        try
        {
            return await SyncOneAsync(itemId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped part way through. The engine writes to a temporary file and only
            // moves it into place at the end, so the subtitle on disk is untouched.
            SetItemStatus(itemId, QueueItemStatus.Cancelled);
            SaveRecord(itemId, MovieSyncStatus.Pending, "Stopped before this item finished");
            return false;
        }
        finally
        {
            _runGate.Release();
        }
    }

    // The token every item in the current job runs under.
    private CancellationToken JobToken
    {
        get
        {
            lock (_lock)
            {
                return _jobCancellation.Token;
            }
        }
    }

    // Called under _lock whenever a fresh job starts, so a Stop from the last one doesn't
    // kill the new one on its way out of the gate.
    private void ResetCancellation()
    {
        if (_jobCancellation.IsCancellationRequested)
        {
            _jobCancellation.Dispose();
            _jobCancellation = new CancellationTokenSource();
        }

        _cancelRequested = false;
    }

    private async Task<bool> SyncOneAsync(Guid itemId, CancellationToken cancellationToken)
    {
        SetItemStatus(itemId, QueueItemStatus.Running);

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "Item not found or has no video file");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return false;
        }

        // Background runs - bulk, scheduled, the Radarr/Sonarr webhook - only ever touch
        // subtitles that are already files. An embedded track came with the release and is
        // usually right already, so quietly rewriting every one of them across a whole
        // library on a schedule risks doing more harm than the drift it might fix. Syncing
        // one is still there, it just has to be a deliberate press on that one item.
        var subtitles = _subtitleLocator.GetExternalSubtitles(item)
            .FindAll(s => !s.IsEmbedded && s.Supported);

        if (subtitles.Count == 0)
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, "No external subtitle found");
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return false;
        }

        string? lastError = null;
        string? lastSkip = null;

        // Set by a skip the engine wasn't sure about, as opposed to one that was skipped
        // for being right already. Only the first kind leaves the item unfinished.
        var doubted = false;
        SyncResult? lastResult = null;
        var syncedPaths = new List<string>();

        var engine = _registry.GetDefault();
        var penalty = EngineRunner.ResolvePenalty(engine, null);

        // Same mode a manual Sync press uses, so a bulk run and a single press on the same
        // item can't quietly do two different things.
        var mode = EngineRunner.ResolveDefaultMode(engine);

        // A reference job lines each item's other subtitles up against one of its own,
        // rather than against the audio. Much faster, and more accurate as long as the
        // reference track really is correct.
        var referenceKey = _referenceKey;
        SubtitleOption? reference = null;

        if (referenceKey is not null)
        {
            reference = SeriesSyncService.MatchReference(item, subtitles, referenceKey);
            if (reference is null)
            {
                SaveRecord(itemId, MovieSyncStatus.Failed, $"No '{referenceKey}' subtitle on this item to line the others up against");
                SetItemStatus(itemId, QueueItemStatus.Failed);
                return false;
            }
        }

        var action = Plugin.Instance?.Configuration.AutomationAction ?? AutomationAction.Sync;
        var converted = 0;

        foreach (var subtitle in subtitles)
        {
            if (reference is not null && string.Equals(subtitle.Path, reference.Path, StringComparison.Ordinal))
            {
                // never sync the reference against itself
                continue;
            }

            var workPath = subtitle.Path;

            // Converting first is a deliberate choice now rather than a necessity. The
            // engine reads what it reads either way, and the runner converts on its own
            // when it has to; this is the admin saying they want one format on disk.
            if (action is AutomationAction.Convert or AutomationAction.ConvertThenSync)
            {
                var (convertedPath, convertError, didWrite) = await ConvertForAutomationAsync(subtitle, cancellationToken)
                    .ConfigureAwait(false);

                if (convertError is not null)
                {
                    lastError = convertError;
                    _logger.LogWarning("Converting {Subtitle} for {Item} failed: {Error}", subtitle.Path, item.Name, convertError);
                    continue;
                }

                workPath = convertedPath;
                if (didWrite)
                {
                    converted++;
                }
            }

            if (action == AutomationAction.Convert)
            {
                // The whole job for this file was the conversion. Syncing is somebody
                // else's press, or another run with a different action set.
                await TranslateForAutomationAsync(item, workPath, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var referencePath = reference?.Path ?? item.Path;

            var result = await _runner
                .RunAsync(engine, referencePath, workPath, mode, penalty, outputMode: null, destinationOverride: null, outputFormat: null, cancellationToken)
                .ConfigureAwait(false);
            lastResult = result;

            if (!result.Success)
            {
                lastError = result.Error;
                _logger.LogWarning("Sync failed for {Item} ({Subtitle}): {Error}", item.Name, workPath, result.Error);
            }
            else if (result.Skipped)
            {
                lastSkip = DescribeSkip(result);

                // Nothing was rewritten, but an already-in-sync subtitle is a correct
                // subtitle, so it counts as done rather than leaving the item looking
                // half finished on every run that goes past it.
                if (result.AlreadyInSync)
                {
                    syncedPaths.Add(workPath);
                }
                else
                {
                    doubted = true;
                }
            }
            else
            {
                // Only a subtitle we actually rewrote counts. A failure and a deliberate
                // low-confidence skip both leave the file as it was, so neither of them
                // should make the item look any more synced than it was before.
                syncedPaths.Add(workPath);

                // Translate what the sync produced rather than what it read, so the
                // translation carries the corrected timings.
                await TranslateForAutomationAsync(item, result.OutputPath ?? workPath, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
        {
            SaveRecord(itemId, MovieSyncStatus.Failed, lastError, lastResult, syncedPaths);
            SetItemStatus(itemId, QueueItemStatus.Failed);
            return false;
        }

        if (action == AutomationAction.Convert)
        {
            // Nothing here was synced, and saying otherwise would misreport what's on
            // disk. The item stays where it was with a line explaining why.
            var detail = converted == 0
                ? "Everything here was already in the conversion format, and the automation action is set to convert only, so nothing was synced."
                : $"Converted {converted} subtitle{(converted == 1 ? string.Empty : "s")}. The automation action is set to convert only, so nothing was synced.";

            SaveRecord(itemId, MovieSyncStatus.Pending, detail);
            SetItemStatus(itemId, QueueItemStatus.Done);
            return true;
        }

        if (lastSkip is not null)
        {
            // The run worked, we just deliberately didn't write anything. Calling that
            // "Synced" would be a lie about what's on disk, and calling it "Failed" would
            // be a lie about the engine, so it stays pending with the reason attached.
            // Unless nothing was written because every subtitle here was already right,
            // which is the one skip that really is a finished item.
            var status = doubted ? MovieSyncStatus.Pending : MovieSyncStatus.Synced;
            SaveRecord(itemId, status, lastSkip, lastResult, syncedPaths);
            SetItemStatus(itemId, QueueItemStatus.Done);
            return true;
        }

        SaveRecord(itemId, MovieSyncStatus.Synced, null, lastResult, syncedPaths);
        SetItemStatus(itemId, QueueItemStatus.Done);
        return true;
    }

    // Writes the subtitle out in the configured conversion format. Returns the path to
    // carry on with, an error, and whether a new file was actually written.
    private async Task<(string Path, string? Error, bool Written)> ConvertForAutomationAsync(
        SubtitleOption subtitle,
        CancellationToken cancellationToken)
    {
        var format = EngineRunner.ResolveConversionFormat();

        if (string.Equals(subtitle.Format, format, StringComparison.OrdinalIgnoreCase))
        {
            return (subtitle.Path, null, false);
        }

        // A picture based subtitle has no text to convert. That isn't a failure of the
        // run, it just can't take part in this half of it.
        if (_converter.GetConversionProblem(subtitle.Path) is not null)
        {
            return (subtitle.Path, null, false);
        }

        var destination = System.IO.Path.ChangeExtension(subtitle.Path, "." + format);

        // Already converted on an earlier run, so don't pay for it again
        if (File.Exists(destination))
        {
            return (destination, null, false);
        }

        try
        {
            await _converter.ConvertAsync(subtitle.Path, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return (subtitle.Path, "Could not convert " + System.IO.Path.GetFileName(subtitle.Path) + ": " + ex.Message, false);
        }

        if (Plugin.Instance?.Configuration.ConversionReplaceOriginal == true)
        {
            TryDelete(subtitle.Path);
        }

        return (destination, null, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the converted file is written either way
        }
    }

    // Experimental. Off unless someone turned it on and named a language.
    private async Task TranslateForAutomationAsync(BaseItem item, string subtitlePath, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var language = config?.AutoTranslateLanguage?.Trim();

        if (config?.AutoTranslateEnabled != true || string.IsNullOrEmpty(language))
        {
            return;
        }

        if (!SubtitleFormats.IsTextBased(subtitlePath) || !File.Exists(subtitlePath))
        {
            return;
        }

        var destination = SubtitleTextFile.BuildOutputPath(subtitlePath, language);

        if (config.AutoTranslateSkipExisting && File.Exists(destination))
        {
            return;
        }

        var request = new TranslationRequest
        {
            ItemId = item.Id,
            SubtitlePath = subtitlePath,
            TargetLanguage = language
        };

        var result = await _translationService.TranslateAsync(subtitlePath, request, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation(
                "Auto-translated {Subtitle} into {Language} ({Low} of {Total} lines below the threshold)",
                subtitlePath,
                language,
                result.LowConfidenceCount,
                result.LineCount);
        }
        else
        {
            // A translation that failed leaves the synced subtitle untouched, so this is
            // logged rather than being allowed to fail the item.
            _logger.LogWarning("Auto-translating {Subtitle} into {Language} failed: {Error}", subtitlePath, language, result.Error);
        }
    }

    private void SetItemStatus(Guid itemId, QueueItemStatus status)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is not null)
            {
                item.Status = status;
            }
        }
    }

    /// <summary>
    /// Saves (or updates) an item's sync record and persists the plugin config to disk.
    /// Shared by the background queue and the controller's synchronous single-item sync.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="status">The new status.</param>
    /// <param name="error">Error message, if the sync failed.</param>
    /// <param name="result">The engine result, if there is one.</param>
    /// <param name="syncedPaths">The subtitle files that actually got written. Only these
    /// count towards the item being synced, which is what lets an item with four
    /// subtitles and one synced track report as partially synced instead of done.</param>
    public static void SaveRecord(
        Guid itemId,
        MovieSyncStatus status,
        string? error,
        SyncResult? result = null,
        IEnumerable<string>? syncedPaths = null)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var records = plugin.Configuration.MovieRecords;
        var record = records.FirstOrDefault(r => r.ItemId == itemId);
        if (record is null)
        {
            record = new MovieSyncRecord { ItemId = itemId };
            records.Add(record);
        }

        if (syncedPaths is not null)
        {
            foreach (var path in syncedPaths)
            {
                var existing = record.SyncedSubtitles
                    .FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.Ordinal));

                if (existing is null)
                {
                    record.SyncedSubtitles.Add(new SubtitleSyncRecord { Path = path, LastSyncUtc = DateTime.UtcNow });
                }
                else
                {
                    existing.LastSyncUtc = DateTime.UtcNow;
                }
            }
        }

        record.Status = status;
        record.LastSyncUtc = DateTime.UtcNow;
        record.LastError = error;
        record.Mode = result?.Mode;
        record.Penalty = result?.Penalty;
        record.OffsetMs = result?.OffsetMs;
        record.Slope = result?.Slope;
        record.Intercept = result?.Intercept;

        AddHistory(plugin, itemId, status, error, result);

        plugin.SaveConfiguration();
    }

    // One line per file the plugin actually wrote, so it can be put back. A run that
    // deliberately wrote nothing (low confidence, or a failure) has nothing to undo and
    // doesn't get an entry - the item record already carries the reason.
    private static void AddHistory(Plugin plugin, Guid itemId, MovieSyncStatus status, string? error, SyncResult? result)
    {
        if (result is null || !result.Success || result.Skipped || string.IsNullOrEmpty(result.OutputPath))
        {
            return;
        }

        var history = plugin.Configuration.History;

        history.Add(new SyncHistoryEntry
        {
            ItemId = itemId,
            Status = status,
            EngineId = result.EngineId,
            OutputPath = result.OutputPath,
            BackupPath = result.BackupPath,

            // Nothing was replaced, so undoing this means taking away the file it added
            // rather than restoring anything over the top of it.
            WroteNewFile = result.BackupPath is null
                && !string.Equals(result.OutputPath, result.InputPath, StringComparison.Ordinal),
            Detail = error ?? DescribeForHistory(result)
        });

        if (history.Count > PluginConfiguration.MaxHistoryEntries)
        {
            history.RemoveRange(0, history.Count - PluginConfiguration.MaxHistoryEntries);
        }
    }

    private static string DescribeForHistory(SyncResult result)
    {
        var parts = new List<string>();

        if (result.OffsetMs is not null and not 0)
        {
            parts.Add($"moved {result.OffsetMs}ms");
        }

        if (result.Slope.HasValue)
        {
            parts.Add($"stretched {result.Slope.Value * 100:0.###}%");
        }

        if (result.Verdict is not null)
        {
            parts.Add(result.Verdict);
        }

        return parts.Count == 0 ? "synced" : string.Join(", ", parts);
    }
}
