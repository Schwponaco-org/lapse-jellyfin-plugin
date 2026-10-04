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
    private readonly ReadableSubtitleService _readable;
    private readonly MultiEngineSyncService _multiEngine;
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

    // Set and cleared under _lock. Task.IsCompleted can't be used for this: the loop
    // decides to stop inside the lock, but its task only completes after the lock is
    // released, and an item enqueued in that gap would see a worker that looks alive,
    // not start another, and sit in the queue until something else came along.
    private bool _workerRunning;

    // One warm LAPSE process for the job that's running, so a library's worth of files
    // doesn't pay for loading the voice detector once per file. Only touched while
    // _runGate is held, and closed when the queue runs dry.
    private EngineBatchSession? _batch;
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
    /// <param name="readable">Writes readable copies, when that automation is turned on.</param>
    /// <param name="multiEngine">Collects the other engines' answers when LAPSE isn't sure
    /// and unattended runs have been allowed to do that.</param>
    /// <param name="logger">Logger.</param>
    public SyncQueueManager(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SubtitleLocator subtitleLocator,
        EngineRegistry registry,
        EngineRunner runner,
        SubtitleConverter converter,
        Translation.TranslationService translationService,
        ReadableSubtitleService readable,
        MultiEngineSyncService multiEngine,
        ILogger<SyncQueueManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _subtitleLocator = subtitleLocator;
        _registry = registry;
        _runner = runner;
        _converter = converter;
        _translationService = translationService;
        _readable = readable;
        _multiEngine = multiEngine;
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
                // the reference track or the name of whatever ran last, or its items,
                // which would otherwise pile up for as long as the server runs
                _items.Clear();
                _queuedIds.Clear();
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

        try
        {
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
        }
        finally
        {
            await CloseBatchSessionAsync().ConfigureAwait(false);
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
        if (result.CandidateCount > 0)
        {
            return $"LAPSE wasn't sure, so the other engines were asked the same question. "
                + $"{result.CandidateCount} answers are sitting next to the video, and the original subtitle "
                + "has not been touched. Play the item, switch between the subtitle tracks until one lines up, "
                + "then open the subtitle menu and press \"Keep this subtitle\".";
        }

        if (result.AlreadyInSync)
        {
            var tolerance = Plugin.Instance?.Configuration.AlreadyInSyncToleranceMs ?? 100;
            return $"Already in sync (the engine would have moved it {result.OffsetMs}ms, which is inside the "
                + $"{tolerance}ms tolerance), so the file was left exactly as it was.";
        }

        var threshold = Plugin.Instance?.Configuration.ConfidenceSigma ?? LapseEngine.DefaultConfidenceSigma;
        var measured = result.Sigma.HasValue
            ? $" It scored {result.Sigma.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}, "
                + $"and needs {threshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} to be sure."
            : string.Empty;

        var where = result.Unconfirmed && result.OutputPath is not null
            ? $" Its answer was written beside the original as {Path.GetFileName(result.OutputPath)} for you to check, and the original was left alone."
            : " The original was left alone.";

        // Only got through on a second go with --force, which takes the engine's word with
        // nothing to check it against.
        if (result.Forced)
        {
            return "This subtitle has too few lines for LAPSE to check its own answer, so it was only timed with --force." + where
                + " A short track like a forced one is best lined up against the full subtitle in the same language, "
                + "with Sync Subtitles to Reference.";
        }

        // "nothing" is the engine saying the audio doesn't back any answer up, which
        // nearly always means the subtitle is for another release or another film. Telling
        // someone to force that through would write a wrong subtitle over a wrong subtitle,
        // so this one points at getting the right file instead.
        if (string.Equals(result.Verdict, "nothing", StringComparison.OrdinalIgnoreCase))
        {
            return "LAPSE couldn't match this subtitle to the audio at all, which nearly always means it was made "
                + "for a different release or a different film." + measured + where
                + " A subtitle made for this exact release is the fix; forcing this one through won't be.";
        }

        // Anyone reading this has already decided the engine is being too careful, so the
        // way to overrule it goes in the message rather than being left to be found.
        return "LAPSE found an answer but wasn't sure enough of it to replace the subtitle." + measured + where
            + " If you know the subtitle belongs to this video, turn on \"Sync even when the engine is unsure\" "
            + "under Settings, Engines, Advanced.";
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
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
            _ = Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await DrainQueueAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ProcessOneAsync keeps everything it can in, so this is the last line. A
            // worker that dies without saying so leaves the flag set and the queue jammed
            // until a restart, which is worse than the item that broke it.
            _logger.LogError(ex, "The sync queue stopped unexpectedly");

            lock (_lock)
            {
                _workerRunning = false;
            }
        }
    }

    private async Task DrainQueueAsync()
    {
        while (true)
        {
            Guid itemId;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _workerRunning = false;
                    break;
                }

                itemId = _pending.Dequeue();
                _queuedIds.Remove(itemId);

                // Marked here, under the same lock, so there is no moment where the item
                // has left the queue but isn't running yet. Anything enqueued in that gap
                // would take the queue for idle and start a fresh job over the top of it.
                var entry = _items.FirstOrDefault(i => i.ItemId == itemId);
                if (entry is not null)
                {
                    entry.Status = QueueItemStatus.Running;
                }
            }

            await ProcessOneAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }

        await CloseBatchSessionAsync().ConfigureAwait(false);
    }

    // The queue has run dry, so the warm engine process has nothing left to do. Taken
    // under the run gate so it can't be closed underneath an item that's using it; if
    // another run has started meanwhile, the next item it syncs simply starts a new one.
    private async Task CloseBatchSessionAsync()
    {
        await _runGate.WaitAsync().ConfigureAwait(false);

        try
        {
            _batch?.Dispose();
            _batch = null;
        }
        finally
        {
            _runGate.Release();
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
            _batch?.Dispose();
            _batch = null;
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
        catch (Exception ex)
        {
            // Anything else getting out would kill the worker with this item still
            // marked as running, which the queue reads as busy until the server restarts.
            _logger.LogError(ex, "Syncing item {ItemId} failed unexpectedly", itemId);
            SetItemStatus(itemId, QueueItemStatus.Failed);
            TrySaveRecord(itemId, MovieSyncStatus.Failed, ex.Message);
            return false;
        }
        finally
        {
            _runGate.Release();
        }
    }

    // The token every item in the current job runs under.
    private void TrySaveRecord(Guid itemId, MovieSyncStatus status, string error)
    {
        try
        {
            SaveRecord(itemId, status, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the result for item {ItemId}", itemId);
        }
    }

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
        // one is still there, it just has to be a deliberate press on that one item. The
        // same goes for an embedded track that was only extracted to a file and never
        // synced: it's the same track, just beside the video instead of inside it.
        var subtitles = _subtitleLocator.GetExternalSubtitles(item)
            .FindAll(s => !s.IsEmbedded && s.Supported && !SubtitleExtractor.IsUntouchedExtraction(item, s.Path));

        // A sidecar an earlier sync wrote is rewritten when its original is synced, so it
        // isn't synced again as a subtitle of its own.
        var allPaths = subtitles.ConvertAll(s => s.Path);
        subtitles.RemoveAll(s => EngineRunner.IsSidecarOfAnother(s.Path, allPaths));

        if (subtitles.Count == 0)
        {
            // Nothing went wrong, there's just nothing here an unattended run touches.
            // Calling that a failure filled the list with red for every film whose only
            // subtitles are inside the video.
            SaveRecord(
                itemId,
                MovieSyncStatus.Pending,
                "No subtitle file to sync. Runs nobody is watching leave the tracks inside the video alone; sync one by hand to bring it out as a file.");
            SetItemStatus(itemId, QueueItemStatus.Done);
            return true;
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

        // A subtitle that was synced and hasn't been written to since gets the same answer
        // again, so unattended runs leave it alone. A reference job is someone asking for
        // a whole series to be lined up against one track, and does every episode.
        if (referenceKey is null
            && action != AutomationAction.Convert
            && Plugin.Instance?.Configuration.SkipSyncedInUnattendedRuns == true)
        {
            var record = FindRecord(itemId);
            var before = subtitles.Count;
            subtitles.RemoveAll(s => record is not null && IsStillSynced(record, s.Path));

            if (subtitles.Count == 0)
            {
                _logger.LogDebug("{Item} is already synced, leaving its {Count} subtitles alone", item.Name, before);
                SetItemStatus(itemId, QueueItemStatus.Done);
                return true;
            }
        }

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
                await MakeReadableForAutomationAsync(item, workPath, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var referencePath = reference?.Path ?? item.Path;

            // Created here rather than up front so a job that never reaches the engine -
            // nothing but conversions, or no subtitles anywhere - never starts one.
            _batch ??= _runner.CreateBatchSession();

            var result = await _runner
                .RunAsync(engine, referencePath, workPath, mode, penalty, batch: _batch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            lastResult = result;

            // LAPSE wasn't sure. Unattended runs only get to ask the other engines when an
            // admin has said so, because every subtitle it is unsure about leaves two or
            // three more files behind, and a whole library's worth of those adds up.
            if (result.Success && _multiEngine.ShouldBuild(result, unattended: true))
            {
                await _multiEngine
                    .BuildAsync(item, referencePath, workPath, result, cancellationToken)
                    .ConfigureAwait(false);
            }

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
            else if (result.Unconfirmed)
            {
                // Written, but beside the original because the engine had its doubts.
                // That file is waiting for someone to look at it, which is not the same
                // thing as the subtitle being synced.
                lastSkip = DescribeSkip(result);
                doubted = true;
            }
            else
            {
                // Only a subtitle we actually rewrote counts. A failure and a deliberate
                // low-confidence skip both leave the file as it was, so neither of them
                // should make the item look any more synced than it was before. A new file
                // written beside it is synced too, by definition, and counts with it.
                syncedPaths.Add(workPath);

                if (result.OutputPath is { } written && !string.Equals(written, workPath, StringComparison.Ordinal))
                {
                    syncedPaths.Add(written);
                }

                // Translate what the sync produced rather than what it read, so the
                // translation carries the corrected timings.
                await TranslateForAutomationAsync(item, result.OutputPath ?? workPath, cancellationToken).ConfigureAwait(false);

                // Same reasoning: the readable copy is made from the synced file, so it
                // has the corrected timings rather than the ones the sync just fixed.
                await MakeReadableForAutomationAsync(item, result.OutputPath ?? workPath, cancellationToken).ConfigureAwait(false);
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
            await _converter.ConvertAsync(subtitle.Path, destination, cancellationToken: cancellationToken).ConfigureAwait(false);
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

        // An unattended run has nobody to fill the From box in, and leaving it empty is
        // not harmless: providers that validate the code (Lingarr, via CultureInfo)
        // reject the "auto" that stands in for it and fail the whole job. The configured
        // default comes first, then the subtitle's own language tag, which is right far
        // more often than not for a file named Movie.en.srt.
        var source = config.TranslationDefaultSourceLanguage?.Trim();

        if (string.IsNullOrEmpty(source) && SubtitleTextFile.TryGetLanguageTag(subtitlePath, out var tag))
        {
            source = tag;
        }

        var request = new TranslationRequest
        {
            ItemId = item.Id,
            SubtitlePath = subtitlePath,
            SourceLanguage = source,
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

    // Off unless an admin turned it on. When it is on, it either adds a readable track
    // beside each subtitle or replaces it, which is the difference between giving the one
    // person who needs it something to pick and changing what the whole household sees -
    // so it's two settings rather than one switch.
    private async Task MakeReadableForAutomationAsync(BaseItem item, string subtitlePath, CancellationToken cancellationToken)
    {
        var mode = Plugin.Instance?.Configuration.ReadableAutomation ?? ReadableAutomationMode.Off;

        if (mode == ReadableAutomationMode.Off)
        {
            return;
        }

        if (!SubtitleFormats.IsTextBased(subtitlePath) || !File.Exists(subtitlePath))
        {
            return;
        }

        // Restyling a readable subtitle would only write it again under the same name on
        // every run, and in replace mode would back up the styled file over the original.
        if (ReadableSubtitleService.IsReadable(subtitlePath))
        {
            return;
        }

        var result = await _readable
            .MakeReadableAsync(item, subtitlePath, mode == ReadableAutomationMode.Replace, wasEmbedded: false, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation(
                "Wrote a readable {Script} subtitle to {Path}",
                result.Script,
                result.OutputPath);
        }
        else
        {
            // A subtitle that couldn't be restyled is still a synced subtitle, so this is
            // logged rather than allowed to fail the item.
            _logger.LogWarning("Could not make {Path} readable: {Error}", subtitlePath, result.Error);
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

        lock (Plugin.ConfigurationLock)
        {
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
                        record.SyncedSubtitles.Add(new SubtitleSyncRecord { Path = path, LastSyncUtc = DateTime.UtcNow, FileWriteUtc = WriteTimeOf(path) });
                    }
                    else
                    {
                        existing.LastSyncUtc = DateTime.UtcNow;
                        existing.FileWriteUtc = WriteTimeOf(path);
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

            AddHistory(plugin, itemId, status, result);

            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Says whether a subtitle is still the one that was synced: the record has it, and
    /// the file's write time is the one it had after the sync. A subtitle Bazarr or a
    /// person replaced under the same name is a new subtitle, and so is an older copy put
    /// back from a backup. Records from before the write time was kept fall back to
    /// "not written since the sync", with five minutes of slack for a network share whose
    /// clock runs a little ahead of the server's.
    /// </summary>
    /// <param name="record">The item's record.</param>
    /// <param name="path">The subtitle.</param>
    /// <returns>True when it's synced and untouched since.</returns>
    public static bool IsStillSynced(MovieSyncRecord record, string path)
    {
        var synced = record.SyncedSubtitles.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.Ordinal));
        if (synced is null)
        {
            return false;
        }

        if (SubtitleExtractor.IsEmbedded(path))
        {
            return true;
        }

        var written = File.GetLastWriteTimeUtc(path);

        // Both times come off the same file system, so they match exactly unless the file
        // changed. The second of slack is for file systems that keep coarse times.
        return synced.FileWriteUtc is { } recorded
            ? Math.Abs((written - recorded).TotalSeconds) < 1
            : written <= synced.LastSyncUtc.AddMinutes(5);
    }

    private static DateTime? WriteTimeOf(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static MovieSyncRecord? FindRecord(Guid itemId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return null;
        }

        lock (Plugin.ConfigurationLock)
        {
            return plugin.Configuration.MovieRecords.FirstOrDefault(r => r.ItemId == itemId);
        }
    }

    /// <summary>
    /// Moves a synced subtitle's sync time up to now, when LAPSE itself has just written
    /// to it again, so the status list doesn't take the newer file for a replaced one. A
    /// file that isn't recorded as synced is left as it is.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="path">The subtitle file that was written.</param>
    public static void TouchSyncedSubtitle(Guid itemId, string? path)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Plugin.ConfigurationLock)
        {
            var synced = plugin.Configuration.MovieRecords
                .FirstOrDefault(r => r.ItemId == itemId)?
                .SyncedSubtitles.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.Ordinal));

            if (synced is not null)
            {
                synced.LastSyncUtc = DateTime.UtcNow;
                synced.FileWriteUtc = WriteTimeOf(path);
                plugin.SaveConfiguration();
            }
        }
    }

    // One line per file the plugin actually wrote, so it can be put back. A run that
    // deliberately wrote nothing (low confidence, or a failure) has nothing to undo and
    // doesn't get an entry - the item record already carries the reason.
    private static void AddHistory(Plugin plugin, Guid itemId, MovieSyncStatus status, SyncResult? result)
    {
        if (result is null || !result.Success || result.Skipped || string.IsNullOrEmpty(result.OutputPath))
        {
            return;
        }

        SyncHistoryService.Append(plugin.Configuration, new SyncHistoryEntry
        {
            ItemId = itemId,
            Status = status,
            EngineId = result.EngineId,
            OutputPath = result.OutputPath,
            InputPath = result.InputPath,
            BackupPath = result.BackupPath,

            // Nothing was replaced, so undoing this means taking away the file it added
            // rather than restoring anything over the top of it.
            WroteNewFile = result.BackupPath is null
                && !string.Equals(result.OutputPath, result.InputPath, StringComparison.Ordinal),

            // The short version. The item's own record carries the full explanation, and
            // a paragraph here pushed the item's name off its row in the activity list.
            Detail = DescribeForHistory(result)
        });
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

        if (result.Unconfirmed)
        {
            parts.Add("written beside the original to check");
        }

        return parts.Count == 0 ? "synced" : string.Join(", ", parts);
    }
}
