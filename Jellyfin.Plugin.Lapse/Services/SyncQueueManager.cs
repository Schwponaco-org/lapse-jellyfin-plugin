// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Background queue for bulk, scheduled and auto-sync jobs. Runs one item at a time with
/// the default engine and mode against every external subtitle the item has. What it does
/// to each subtitle (sync, convert, both) comes from <see cref="AutomationAction"/>.
/// </summary>
public partial class SyncQueueManager : IDisposable
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
    private readonly SyncHistoryService _historyService;
    private readonly RunNotifier _notifier;
    private readonly ILogger<SyncQueueManager> _logger;
    private readonly object _lock = new();

    // rows in queue order for the dashboard, plus the same rows by id
    private readonly List<QueueItem> _items = new();
    private readonly Dictionary<Guid, QueueItem> _itemsById = new();
    private readonly Queue<Guid> _pending = new();
    private readonly HashSet<Guid> _queuedIds = new();

    // Only one item syncs at a time. Both the worker loop and RunBatchAsync (the scheduled
    // task) go through this, otherwise a library schedule firing during the nightly run
    // would have two engines going over the same files.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    // A scheduled run has gaps between items where nothing is queued or running. Counting
    // the run itself stops the queue from looking idle there and wiping its rows.
    private int _runningCount;
    private int _scheduledRuns;
    private int _finishedCount;

    // Set/cleared under _lock. Can't use Task.IsCompleted, the loop decides to stop inside
    // the lock but its task completes after, and an item enqueued in that gap would get stuck.
    private bool _workerRunning;

    // One warm LAPSE process per job so we don't reload the voice detector per file.
    // Only touched while holding _runGate.
    private EngineBatchSession? _batch;
    private string? _jobName;
    private string? _unitName;
    private string? _referenceKey;

    // The user who started the job when it wasn't an admin. Only they and admins see or stop it.
    private Guid? _startedBy;

    // Stop button. Replaced when the next job starts.
    private CancellationTokenSource _jobCancellation = new();
    private bool _cancelRequested;

    // What the job adds up to, for the notification when it's done. An item synced twice
    // in one job counts once, the way it went the last time.
    private readonly Dictionary<Guid, MovieSyncStatus> _counted = new();
    private bool _runOpen;
    private DateTime _startedUtc;
    private List<SyncHistoryEntry> _runFiles = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueueManager"/> class.
    /// </summary>
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
        SyncHistoryService historyService,
        RunNotifier notifier,
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
        _historyService = historyService;
        _notifier = notifier;
        _logger = logger;
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

    /// <summary>
    /// Builds a progress snapshot for the dashboard.
    /// </summary>
    /// <param name="includeItems">Include a row per item. The progress bar polls every couple of seconds and only needs counts.</param>
    /// <param name="viewer">The user asking when it isn't an admin. Someone else's job looks like no job.</param>
    public QueueSnapshot GetSnapshot(bool includeItems = false, Guid? viewer = null)
    {
        lock (_lock)
        {
            if (viewer is not null && viewer != _startedBy)
            {
                return new QueueSnapshot();
            }

            var current = _runningCount > 0 ? _items.Find(i => i.Status == QueueItemStatus.Running) : null;
            return new QueueSnapshot
            {
                Running = !IsIdleLocked(),
                Total = _items.Count,
                Completed = _finishedCount,
                Cancelling = _cancelRequested && current is not null,
                CurrentItemName = current?.Name,
                JobName = _jobName,
                UnitName = _unitName,
                Items = includeItems ? new List<QueueItem>(_items) : new List<QueueItem>()
            };
        }
    }

    /// <summary>
    /// Starts a bulk job over every enabled library. False if a job is already running.
    /// </summary>
    public bool EnqueueLibrary()
    {
        return StartBulkJob(_libraryService.GetItems());
    }

    /// <summary>
    /// Starts a bulk job for everything under one library or folder.
    /// </summary>
    public bool EnqueueFolder(Guid folderId)
    {
        return StartBulkJob(_libraryService.GetItems(folderId));
    }

    /// <summary>
    /// Starts a bulk job over a given set of items (a series or season). With a reference
    /// key each item's subtitles are lined up against one of its own tracks instead of the audio.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <param name="jobName">Name for the progress readout.</param>
    /// <param name="unitName">What one item is called, e.g. "episode".</param>
    /// <param name="referenceKey">Key from <see cref="SeriesSyncService.GetReferenceOptions"/>, or null for audio.</param>
    /// <param name="startedBy">The user starting it when it isn't an admin.</param>
    public bool EnqueueItems(IReadOnlyList<BaseItem> items, string jobName, string unitName, string? referenceKey = null, Guid? startedBy = null)
    {
        return StartBulkJob(items, jobName, unitName, referenceKey, startedBy);
    }

    /// <summary>
    /// Adds one item to the queue and starts the worker if needed. Used by auto-sync, the
    /// scheduled task and the webhook. Skipped/ignored items are dropped silently, those
    /// lists only stop automatic runs, a hand press still works.
    /// </summary>
    public void EnqueueItem(BaseItem item)
    {
        if (!ItemExclusions.Load().Excludes(item))
        {
            EnqueueRange(new[] { item });
        }
    }

    /// <summary>
    /// Says whether an item is waiting in the queue or syncing right now.
    /// </summary>
    public bool IsQueuedOrRunning(Guid itemId)
    {
        lock (_lock)
        {
            return _queuedIds.Contains(itemId)
                || (_itemsById.TryGetValue(itemId, out var row) && row.Status == QueueItemStatus.Running);
        }
    }

    /// <summary>
    /// Adds a batch of items to whatever's queued, under one lock. The caller has already
    /// dropped skipped/ignored items (e.g. <see cref="LibraryService.GetItems"/> does).
    /// </summary>
    public void EnqueueRange(IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (IsIdleLocked())
            {
                // new job, don't carry the last one's rows or reference track along
                StartFreshJobLocked(null, null, null);
            }
            else
            {
                // automatic items joining a user's own job make it one for admins only
                _startedBy = null;
            }

            foreach (var item in items)
            {
                if (_queuedIds.Add(item.Id))
                {
                    _pending.Enqueue(item.Id);
                    AddRowLocked(item);
                }
            }
        }

        EnsureWorkerRunning();
    }

    /// <summary>
    /// Stops the running job: drops what's queued and cancels the current item (which kills
    /// the engine). Files already written stay written.
    /// </summary>
    /// <param name="viewer">The user asking when it isn't an admin, who may only stop their own job.</param>
    /// <returns>How many queued items were dropped, or null if nothing was running.</returns>
    public int? Cancel(Guid? viewer = null)
    {
        CancellationTokenSource cancellation;
        int dropped;

        lock (_lock)
        {
            if (IsIdleLocked() || (viewer is not null && viewer != _startedBy))
            {
                return null;
            }

            dropped = _pending.Count;
            _pending.Clear();
            _queuedIds.Clear();
            _cancelRequested = true;

            foreach (var row in _items)
            {
                if (row.Status == QueueItemStatus.Queued)
                {
                    row.Status = QueueItemStatus.Cancelled;
                    _finishedCount++;
                }
            }

            cancellation = _jobCancellation;
        }

        _logger.LogInformation("Sync job stopped by hand, {Dropped} queued items dropped", dropped);

        // outside the lock, the continuations on this token want the lock too
        cancellation.Cancel();
        return dropped;
    }

    /// <summary>
    /// Runs a batch and waits for it. The scheduled task uses this since Jellyfin's task
    /// runner wants something that stays alive as long as the work does.
    /// </summary>
    /// <returns>How many items synced without an error.</returns>
    public async Task<int> RunBatchAsync(
        IReadOnlyList<BaseItem> items,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var succeeded = 0;
        BulkRun? finished = null;

        lock (_lock)
        {
            if (IsIdleLocked())
            {
                StartFreshJobLocked("Scheduled sync", null, null);
            }

            _scheduledRuns++;
        }

        // listens to both Jellyfin's token and our Stop button
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
                    AddRowLocked(item);
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
            lock (_lock)
            {
                _scheduledRuns--;
                finished = TakeFinishedRunLocked();
            }

            await CloseBatchSessionAsync().ConfigureAwait(false);
            Plugin.Instance?.FlushDeferredSave();
            Report(finished);
        }

        return succeeded;
    }

    /// <summary>
    /// Explains a result we deliberately didn't write, for the item list.
    /// </summary>
    public static string DescribeSkip(SyncResult result)
    {
        if (result.CandidateCount > 0)
        {
            return "LAPSE wasn't sure, so the other engines were asked the same question. "
                + $"{result.CandidateCount} answers are sitting next to the video, and the original subtitle "
                + "has not been touched. Play the item, switch between the subtitle tracks until one lines up, "
                + "then open the subtitle menu and press \"Keep this subtitle\".";
        }

        if (result.AlreadyInSync)
        {
            var tolerance = Plugin.Instance?.Configuration.AlreadyInSyncToleranceMs ?? 100;
            var moved = result.PartOffsetsMs is { Count: > 1 } split
                ? "moved its parts " + string.Join(", ", split.Select(Seconds))
                : "moved it " + Seconds(result.OffsetMs ?? 0);
            return $"Already in sync (the engine would have {moved}, which is inside the "
                + $"{tolerance}ms tolerance), so the file was left exactly as it was.";
        }

        var threshold = Plugin.Instance?.Configuration.ConfidenceSigma ?? LapseEngine.DefaultConfidenceSigma;
        var measured = result.Sigma.HasValue
            ? $" It scored {result.Sigma.Value.ToString("0.#", CultureInfo.InvariantCulture)}, "
                + $"and needs {threshold.ToString("0.#", CultureInfo.InvariantCulture)} to be sure."
            : string.Empty;

        var where = result.Unconfirmed && result.OutputPath is not null
            ? $" Its answer was written beside the original as {Path.GetFileName(result.OutputPath)} for you to check, and the original was left alone."
            : " The original was left alone.";

        // only got through on a second try with --force
        if (result.Forced)
        {
            return "This subtitle has too few lines for LAPSE to check its own answer, so it was only timed with --force." + where
                + " A short track like a forced one is best lined up against the full subtitle in the same language, "
                + "with Sync Subtitles to Reference.";
        }

        // "nothing" almost always means a subtitle for another release, forcing won't help
        if (string.Equals(result.Verdict, "nothing", StringComparison.OrdinalIgnoreCase))
        {
            return "LAPSE couldn't match this subtitle to the audio at all, which nearly always means it was made "
                + "for a different release or a different film." + measured + where
                + " A subtitle made for this exact release is the fix; forcing this one through won't be.";
        }

        return "LAPSE found an answer but wasn't sure enough of it to replace the subtitle." + measured + where
            + " If you know the subtitle belongs to this video, turn on \"Sync even when the engine is unsure\" "
            + "under Settings, Engines, Advanced.";
    }

    /// <summary>
    /// Name for the queue list. Episodes get series and SxxEyy since "Episode 3" alone is useless.
    /// </summary>
    public static string DescribeItem(BaseItem item)
    {
        var name = item.Name ?? "Unknown";

        if (item is not Episode episode)
        {
            return name;
        }

        var numbers = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? $"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} "
            : string.Empty;

        return string.IsNullOrEmpty(episode.SeriesName) ? numbers + name : $"{episode.SeriesName} - {numbers}{name}";
    }

    /// <summary>
    /// Saves an item's sync record. Shared by the queue and the single item sync in the controller.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="status">New status.</param>
    /// <param name="error">Error message, if it failed.</param>
    /// <param name="result">Engine result, if any.</param>
    /// <param name="syncedPaths">Files that actually got written. Only these count as synced.</param>
    /// <param name="deferSave">Let the disk write wait a few seconds so a bulk run doesn't rewrite the config per item.</param>
    /// <param name="results">Every engine result when there was more than one subtitle, each file written gets a history entry. Defaults to <paramref name="result"/>.</param>
    /// <returns>The history entries added.</returns>
    public static List<SyncHistoryEntry> SaveRecord(
        Guid itemId,
        MovieSyncStatus status,
        string? error,
        SyncResult? result = null,
        IEnumerable<string>? syncedPaths = null,
        bool deferSave = false,
        IEnumerable<SyncResult?>? results = null)
    {
        var added = new List<SyncHistoryEntry>();
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return added;
        }

        lock (Plugin.ConfigurationLock)
        {
            var record = plugin.Configuration.GetOrAddRecord(itemId);
            var now = DateTime.UtcNow;

            if (syncedPaths is not null)
            {
                foreach (var path in syncedPaths)
                {
                    var existing = record.SyncedSubtitles.Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));
                    if (existing is null)
                    {
                        existing = new SubtitleSyncRecord { Path = path };
                        record.SyncedSubtitles.Add(existing);
                    }

                    existing.LastSyncUtc = now;
                    existing.FileWriteUtc = WriteTimeOf(path);
                }
            }

            record.Status = status;
            record.LastSyncUtc = now;
            record.LastError = error;
            record.Mode = result?.Mode;
            record.Penalty = result?.Penalty;
            record.OffsetMs = result?.OffsetMs;
            record.Slope = result?.Slope;
            record.Intercept = result?.Intercept;

            foreach (var written in results ?? new[] { result })
            {
                if (AddHistory(plugin, itemId, status, written) is { } entry)
                {
                    added.Add(entry);
                }
            }

            if (deferSave)
            {
                plugin.SaveConfigurationSoon();
            }
            else
            {
                plugin.SaveConfiguration();
            }
        }

        return added;
    }

    /// <summary>
    /// Says whether a subtitle is still the one we synced: it's in the record and the file
    /// hasn't been written since. A file Bazarr swapped under the same name is a new subtitle.
    /// </summary>
    public static bool IsStillSynced(MovieSyncRecord record, string path)
    {
        var synced = record.SyncedSubtitles.Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));
        if (synced is null)
        {
            return false;
        }

        if (SubtitleExtractor.IsEmbedded(path))
        {
            return true;
        }

        var written = File.GetLastWriteTimeUtc(path);

        // Same file system so the times match exactly unless the file changed. 1s slack for
        // coarse timestamps. Old records without a write time get 5 min for clock skew on shares.
        return synced.FileWriteUtc is { } recorded
            ? Math.Abs((written - recorded).TotalSeconds) < 1
            : written <= synced.LastSyncUtc.AddMinutes(5);
    }

    /// <summary>
    /// Bumps a synced subtitle's sync time after LAPSE itself wrote to it again, so the
    /// status list doesn't think it was replaced. Does nothing for files not recorded as synced.
    /// </summary>
    public static void TouchSyncedSubtitle(Guid itemId, string? path)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Plugin.ConfigurationLock)
        {
            var synced = plugin.Configuration.FindRecord(itemId)?
                .SyncedSubtitles.Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));

            if (synced is not null)
            {
                synced.LastSyncUtc = DateTime.UtcNow;
                synced.FileWriteUtc = WriteTimeOf(path);
                plugin.SaveConfiguration();
            }
        }
    }

    /// <summary>
    /// Puts a shift by hand in the history, so it can be undone from the dashboard like a sync.
    /// Left out when it went over the subtitle with no backup, there's nothing to undo then.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="inputPath">The subtitle that was shifted.</param>
    /// <param name="shifted">What the shift wrote.</param>
    /// <param name="offsetMs">How far it was moved, positive is later.</param>
    public static void RecordShift(Guid itemId, string inputPath, ShiftResult shifted, int offsetMs)
    {
        var plugin = Plugin.Instance;
        var wroteNewFile = shifted.BackupPath is null && !string.Equals(shifted.OutputPath, inputPath, StringComparison.Ordinal);
        if (plugin is null || (shifted.BackupPath is null && !wroteNewFile))
        {
            return;
        }

        lock (Plugin.ConfigurationLock)
        {
            SyncHistoryService.Append(plugin.Configuration, new SyncHistoryEntry
            {
                ItemId = itemId,
                Status = MovieSyncStatus.Synced,
                OutputPath = shifted.OutputPath,

                // no InputPath: undoing a shift must not make a synced original count as unsynced
                BackupPath = shifted.BackupPath,
                WroteNewFile = wroteNewFile,
                OutputWriteUtc = WriteTimeOf(shifted.OutputPath),
                Detail = "Shifted " + Seconds(offsetMs) + " by hand."
            });

            plugin.SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases what this holds.
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
            Plugin.Instance?.FlushDeferredSave();
        }
    }

    private bool StartBulkJob(
        IReadOnlyList<BaseItem> items,
        string? jobName = null,
        string? unitName = null,
        string? referenceKey = null,
        Guid? startedBy = null)
    {
        if (items.Count == 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (!IsIdleLocked())
            {
                return false;
            }

            StartFreshJobLocked(jobName, unitName, referenceKey);
            _startedBy = startedBy;

            foreach (var item in items)
            {
                if (_queuedIds.Add(item.Id))
                {
                    _pending.Enqueue(item.Id);
                    AddRowLocked(item);
                }
            }
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
            BulkRun? finished = null;

            while (true)
            {
                Guid itemId;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        _workerRunning = false;
                        finished = TakeFinishedRunLocked();
                        break;
                    }

                    itemId = _pending.Dequeue();
                    _queuedIds.Remove(itemId);

                    // mark it running under the same lock, otherwise there's a moment where
                    // the queue looks idle and a new item would start a fresh job over this one
                    SetStatusLocked(itemId, QueueItemStatus.Running);
                }

                await ProcessOneAsync(itemId, CancellationToken.None).ConfigureAwait(false);
            }

            await CloseBatchSessionAsync().ConfigureAwait(false);
            Plugin.Instance?.FlushDeferredSave();
            Report(finished);
        }
        catch (Exception ex)
        {
            // ProcessOneAsync catches what it can, this is the last line. A worker dying
            // silently would leave the flag set and the queue jammed until a restart.
            _logger.LogError(ex, "The sync queue stopped unexpectedly");

            lock (_lock)
            {
                _workerRunning = false;
            }
        }
    }

    // Close the warm engine when the queue runs dry. Under the run gate so it can't close
    // under an item that's using it. If another run started meanwhile it just opens a new one.
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

    private async Task<bool> ProcessOneAsync(Guid itemId, CancellationToken cancellationToken)
    {
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
            // the engine writes to a temp file and moves it at the end, so nothing on disk changed
            SetItemStatus(itemId, QueueItemStatus.Cancelled);
            SaveRecord(itemId, MovieSyncStatus.Pending, "Stopped before this item finished", deferSave: true);
            return false;
        }
        catch (Exception ex)
        {
            // letting this out would kill the worker with the item still marked running
            _logger.LogError(ex, "Syncing item {ItemId} failed unexpectedly", itemId);
            SetItemStatus(itemId, QueueItemStatus.Failed);
            Count(itemId, MovieSyncStatus.Failed);

            try
            {
                SaveRecord(itemId, MovieSyncStatus.Failed, ex.Message, deferSave: true);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Could not record the result for item {ItemId}", itemId);
            }

            return false;
        }
        finally
        {
            _runGate.Release();
        }
    }

    // nothing queued, nothing running, no scheduled run between two of its items
    private bool IsIdleLocked()
    {
        return _pending.Count == 0 && _runningCount == 0 && _scheduledRuns == 0;
    }

    // Clears the last job for a new one. Also resets a Stop pressed on the old job so it
    // doesn't kill the new one on the way in.
    private void StartFreshJobLocked(string? jobName, string? unitName, string? referenceKey)
    {
        _items.Clear();
        _itemsById.Clear();
        _queuedIds.Clear();
        _runningCount = 0;
        _finishedCount = 0;
        _jobName = jobName;
        _unitName = unitName ?? "item";
        _referenceKey = referenceKey;
        _startedBy = null;

        if (_jobCancellation.IsCancellationRequested)
        {
            _jobCancellation.Dispose();
            _jobCancellation = new CancellationTokenSource();
        }

        _cancelRequested = false;

        _runOpen = true;
        _startedUtc = DateTime.UtcNow;
        _counted.Clear();
        _runFiles = new List<SyncHistoryEntry>();
    }

    // The job's summary once the queue has gone idle, null while there's more to come. One
    // item on its own (a new episode, a webhook call) isn't a bulk run.
    private BulkRun? TakeFinishedRunLocked()
    {
        if (!_runOpen || !IsIdleLocked())
        {
            return null;
        }

        _runOpen = false;

        if (_items.Count < 2)
        {
            return null;
        }

        return new BulkRun
        {
            Name = _jobName ?? "Bulk sync",
            StartedUtc = _startedUtc,
            FinishedUtc = DateTime.UtcNow,
            Synced = _counted.Values.Count(s => s == MovieSyncStatus.Synced),
            Unsure = _counted.Values.Count(s => s == MovieSyncStatus.Pending),
            Failed = _counted.Values.Count(s => s == MovieSyncStatus.Failed),
            Skipped = _items.Count - _counted.Count,
            Stopped = _cancelRequested,
            Files = _runFiles
        };
    }

    private void Report(BulkRun? run)
    {
        if (run is null)
        {
            return;
        }

        // nothing written, nothing to undo
        if (run.Files.Count > 0)
        {
            _historyService.SaveRun(run);
        }

        _ = Task.Run(() => _notifier.NotifyAsync(run));
    }

    private void AddRowLocked(BaseItem item)
    {
        if (_itemsById.ContainsKey(item.Id))
        {
            return;
        }

        var row = new QueueItem { ItemId = item.Id, Name = DescribeItem(item) };
        _items.Add(row);
        _itemsById[item.Id] = row;
    }

    // Changes a row's status and keeps the running/finished counters right.
    private void SetStatusLocked(Guid itemId, QueueItemStatus status)
    {
        if (!_itemsById.TryGetValue(itemId, out var row) || row.Status == status)
        {
            return;
        }

        if (row.Status == QueueItemStatus.Running)
        {
            _runningCount--;
        }
        else if (status == QueueItemStatus.Running)
        {
            _runningCount++;
        }

        if (IsFinished(row.Status) != IsFinished(status))
        {
            _finishedCount += IsFinished(status) ? 1 : -1;
        }

        row.Status = status;
    }

    private static bool IsFinished(QueueItemStatus status)
    {
        return status is QueueItemStatus.Done or QueueItemStatus.Failed or QueueItemStatus.Cancelled;
    }

    // Pending is what an unsure item counts as, null takes the item back out of the count.
    private void Count(Guid itemId, MovieSyncStatus? outcome)
    {
        lock (_lock)
        {
            if (outcome is { } counted)
            {
                _counted[itemId] = counted;
            }
            else
            {
                _counted.Remove(itemId);
            }
        }
    }

    private void SetItemStatus(Guid itemId, QueueItemStatus status)
    {
        lock (_lock)
        {
            SetStatusLocked(itemId, status);
        }
    }

    // Saves the record, marks the row and says whether it counts as a success.
    private bool Finish(Guid itemId, MovieSyncStatus status, string? message, List<SyncResult>? results = null, List<string>? syncedPaths = null, bool unsure = false)
    {
        var written = SaveRecord(itemId, status, message, results?.LastOrDefault(), syncedPaths, deferSave: true, results);
        lock (_lock)
        {
            _runFiles.AddRange(written);
        }

        var failed = status == MovieSyncStatus.Failed;
        Count(itemId, failed || status == MovieSyncStatus.Synced ? status : unsure ? MovieSyncStatus.Pending : (MovieSyncStatus?)null);

        SetItemStatus(itemId, failed ? QueueItemStatus.Failed : QueueItemStatus.Done);
        return !failed;
    }

    private async Task<bool> SyncOneAsync(Guid itemId, CancellationToken cancellationToken)
    {
        SetItemStatus(itemId, QueueItemStatus.Running);

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return Finish(itemId, MovieSyncStatus.Failed, "Item not found or has no video file");
        }

        // Unattended runs only touch subtitle files. Embedded tracks usually came right with
        // the release and rewriting a whole library of them on a timer can do more harm than
        // good. Same for a track we only extracted and never synced.
        var record = FindRecord(itemId);
        var subtitles = _subtitleLocator.GetExternalSubtitles(item)
            .FindAll(s => !s.IsEmbedded && s.Supported && !SubtitleExtractor.IsUntouchedExtraction(item, s.Path, record));

        // a sidecar from an earlier sync gets rewritten along with its original
        var allPaths = subtitles.ConvertAll(s => s.Path);
        subtitles.RemoveAll(s => EngineRunner.IsSidecarOfAnother(s.Path, allPaths));

        if (subtitles.Count == 0)
        {
            // not a failure, there's just nothing here for an unattended run
            return Finish(
                itemId,
                MovieSyncStatus.Pending,
                "No subtitle file to sync. Runs nobody is watching leave the tracks inside the video alone; sync one by hand to bring it out as a file.");
        }

        string? referenceKey;
        lock (_lock)
        {
            referenceKey = _referenceKey;
        }

        // reference job: line subtitles up against one of the item's own tracks instead of the audio
        SubtitleOption? reference = null;
        if (referenceKey is not null)
        {
            reference = SeriesSyncService.MatchReference(item, subtitles, referenceKey);
            if (reference is null)
            {
                return Finish(itemId, MovieSyncStatus.Failed, $"No '{referenceKey}' subtitle on this item to line the others up against");
            }
        }

        var action = Plugin.Instance?.Configuration.AutomationAction ?? AutomationAction.Sync;

        // Synced and untouched since means the same answer again, skip it. Reference jobs
        // always do every episode since someone asked for exactly that.
        if (referenceKey is null
            && record is not null
            && action != AutomationAction.Convert
            && Plugin.Instance?.Configuration.SkipSyncedInUnattendedRuns == true)
        {
            var before = subtitles.Count;
            subtitles.RemoveAll(s => IsStillSynced(record, s.Path));

            if (subtitles.Count == 0)
            {
                _logger.LogDebug("{Item} is already synced, leaving its {Count} subtitles alone", item.Name, before);
                SetItemStatus(itemId, QueueItemStatus.Done);
                return true;
            }
        }

        var engine = _registry.GetDefault();
        var penalty = EngineRunner.ResolvePenalty(engine, null);
        var mode = EngineRunner.ResolveDefaultMode(engine); // same as a manual Sync press

        string? lastError = null;
        string? lastSkip = null;
        var doubted = false; // skipped because the engine was unsure, not because it was already right
        var results = new List<SyncResult>();
        var syncedPaths = new List<string>();
        var converted = 0;

        foreach (var subtitle in subtitles)
        {
            if (reference is not null && string.Equals(subtitle.Path, reference.Path, StringComparison.Ordinal))
            {
                continue;
            }

            var workPath = subtitle.Path;

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
                await AfterSyncAsync(item, workPath, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var referencePath = reference?.Path ?? item.Path;

            // made lazily so a job that never reaches the engine never starts one
            _batch ??= _runner.CreateBatchSession();

            var result = await _runner
                .RunAsync(engine, referencePath, workPath, mode, penalty, batch: _batch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);

            // only when an admin allowed it for unattended runs, each unsure subtitle leaves 2-3 extra files
            if (result.Success && _multiEngine.ShouldBuild(result, unattended: true))
            {
                await _multiEngine.BuildAsync(item, referencePath, workPath, result, cancellationToken).ConfigureAwait(false);
            }

            _multiEngine.AddForReview(item, engine, workPath, result);

            if (!result.Success)
            {
                lastError = result.Error;
                _logger.LogWarning("Sync failed for {Item} ({Subtitle}): {Error}", item.Name, workPath, result.Error);
            }
            else if (result.Skipped || result.Unconfirmed)
            {
                lastSkip = DescribeSkip(result);

                // already in sync is a correct subtitle, so it counts as done
                if (result.Skipped && result.AlreadyInSync)
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
                syncedPaths.Add(workPath);
                if (result.OutputPath is { } written && !string.Equals(written, workPath, StringComparison.Ordinal))
                {
                    syncedPaths.Add(written);
                }

                // translate / restyle the synced output so they get the fixed timings
                await AfterSyncAsync(item, result.OutputPath ?? workPath, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
        {
            return Finish(itemId, MovieSyncStatus.Failed, lastError, results, syncedPaths);
        }

        if (action == AutomationAction.Convert)
        {
            var detail = converted == 0
                ? "Everything here was already in the conversion format, and the automation action is set to convert only, so nothing was synced."
                : $"Converted {converted} subtitle{(converted == 1 ? string.Empty : "s")}. The automation action is set to convert only, so nothing was synced.";

            return Finish(itemId, MovieSyncStatus.Pending, detail);
        }

        // A skip isn't "synced" (nothing was written) and isn't "failed" (the engine worked).
        // Stays pending with the reason, unless every subtitle was simply already right.
        if (lastSkip is not null)
        {
            return Finish(itemId, doubted ? MovieSyncStatus.Pending : MovieSyncStatus.Synced, lastSkip, results, syncedPaths, doubted);
        }

        return Finish(itemId, MovieSyncStatus.Synced, null, results, syncedPaths);
    }

    // Writes the subtitle in the configured conversion format. Returns the path to carry
    // on with, an error, and whether a file was actually written.
    private async Task<(string Path, string? Error, bool Written)> ConvertForAutomationAsync(
        SubtitleOption subtitle,
        CancellationToken cancellationToken)
    {
        var format = EngineRunner.ResolveConversionFormat();

        // same format already, or a picture subtitle with no text to convert
        if (string.Equals(subtitle.Format, format, StringComparison.OrdinalIgnoreCase)
            || _converter.GetConversionProblem(subtitle.Path) is not null)
        {
            return (subtitle.Path, null, false);
        }

        var destination = Path.ChangeExtension(subtitle.Path, "." + format);

        // converted on an earlier run
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
            return (subtitle.Path, "Could not convert " + Path.GetFileName(subtitle.Path) + ": " + ex.Message, false);
        }

        if (Plugin.Instance?.Configuration.ConversionReplaceOriginal == true)
        {
            try
            {
                File.Delete(subtitle.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the converted file is there either way
            }
        }

        return (destination, null, true);
    }

    // Optional extras once a subtitle is done. Neither of these fails the item.
    private async Task AfterSyncAsync(BaseItem item, string subtitlePath, CancellationToken cancellationToken)
    {
        if (!SubtitleFormats.IsTextBased(subtitlePath) || !File.Exists(subtitlePath))
        {
            return;
        }

        await TranslateForAutomationAsync(item, subtitlePath, cancellationToken).ConfigureAwait(false);
        await MakeReadableForAutomationAsync(item, subtitlePath, cancellationToken).ConfigureAwait(false);
    }

    // Experimental, off unless turned on with a language.
    private async Task TranslateForAutomationAsync(BaseItem item, string subtitlePath, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var language = config?.AutoTranslateLanguage?.Trim();

        if (config?.AutoTranslateEnabled != true || string.IsNullOrEmpty(language))
        {
            return;
        }

        var destination = SubtitleTextFile.BuildOutputPath(subtitlePath, language);
        if (config.AutoTranslateSkipExisting && File.Exists(destination))
        {
            return;
        }

        // Nobody's here to fill in the source language, and some providers (Lingarr) reject
        // "auto". Use the configured default, then the file's language tag (Movie.en.srt).
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
            _logger.LogWarning("Auto-translating {Subtitle} into {Language} failed: {Error}", subtitlePath, language, result.Error);
        }
    }

    // Off unless turned on. Add mode puts a readable track next to the subtitle, replace
    // mode swaps it out for everyone.
    private async Task MakeReadableForAutomationAsync(BaseItem item, string subtitlePath, CancellationToken cancellationToken)
    {
        var mode = Plugin.Instance?.Configuration.ReadableAutomation ?? ReadableAutomationMode.Off;

        // restyling a readable file again would just rewrite it every run
        if (mode == ReadableAutomationMode.Off || ReadableSubtitleService.IsReadable(subtitlePath))
        {
            return;
        }

        var result = await _readable
            .MakeReadableAsync(item, subtitlePath, mode == ReadableAutomationMode.Replace, wasEmbedded: false, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation("Wrote a readable {Script} subtitle to {Path}", result.Script, result.OutputPath);
        }
        else
        {
            _logger.LogWarning("Could not make {Path} readable: {Error}", subtitlePath, result.Error);
        }
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

    internal static MovieSyncRecord? FindRecord(Guid itemId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return null;
        }

        lock (Plugin.ConfigurationLock)
        {
            return plugin.Configuration.FindRecord(itemId)?.Snapshot();
        }
    }

    // One entry per file we actually wrote, so it can be undone. Skips and failures wrote
    // nothing and get no entry.
    private static SyncHistoryEntry? AddHistory(Plugin plugin, Guid itemId, MovieSyncStatus status, SyncResult? result)
    {
        if (result is null || !result.Success || result.Skipped || string.IsNullOrEmpty(result.OutputPath))
        {
            return null;
        }

        var entry = new SyncHistoryEntry
        {
            ItemId = itemId,
            Status = status,
            EngineId = result.EngineId,
            OutputPath = result.OutputPath,
            InputPath = result.InputPath,
            BackupPath = result.BackupPath,

            // nothing replaced means undo = delete the new file. Same for an extracted copy
            // we wrote over, its original is still in the video
            WroteNewFile = result.BackupPath is null
                && (result.ReplacedExtractedCopy
                    || !string.Equals(result.OutputPath, result.InputPath, StringComparison.Ordinal)),

            OutputWriteUtc = WriteTimeOf(result.OutputPath),

            // keep it short, a paragraph here pushed the name off the row
            Detail = DescribeForHistory(result),
            Unsure = result.Unconfirmed
        };

        SyncHistoryService.Append(plugin.Configuration, entry);
        return entry;
    }

    // Read on the dashboard by people who never see the engine, so plain words, not its output
    internal static string DescribeForHistory(SyncResult result)
    {
        var moved = DescribeMove(result);

        return result.Unconfirmed
            ? moved + ". Written beside the original for you to check."
            : moved + ".";
    }

    // "Moved 4.2 s earlier", the same words in the history and under Review
    internal static string DescribeMove(SyncResult result)
    {
        string moved;

        // a split file's one offset_ms only covers one part, 2.3.0 says where each went
        if (result.PartOffsetsMs is { Count: > 1 } split)
        {
            moved = $"Moved in {split.Count} parts, from {Seconds(split.Min())} to {Seconds(split.Max())}";
        }
        else if (result.OffsetMs is not null and not 0)
        {
            moved = "Moved " + Seconds(result.OffsetMs.Value);
        }
        else
        {
            moved = result.Slope.HasValue ? "Stretched to fit" : "Lined up";
        }

        if (string.Equals(result.Verdict, "nothing", StringComparison.Ordinal))
        {
            moved = "No match in the audio";
        }

        return moved;
    }

    // History written before the plain words, "moved -8707ms, nothing, written beside the original to check"
    internal static string? ReadableDetail(string? detail)
    {
        var old = detail is null ? null : OldDetailRegex().Match(detail);
        if (old is null || !old.Success)
        {
            return detail;
        }

        return DescribeForHistory(new SyncResult
        {
            OffsetMs = int.Parse(old.Groups["ms"].Value, CultureInfo.InvariantCulture),
            Verdict = old.Groups["verdict"].Success ? old.Groups["verdict"].Value : null,
            Unconfirmed = old.Groups["check"].Success
        });
    }

    [GeneratedRegex(@"^moved (?<ms>-?\d+)ms(, (?<verdict>solid|unsure|nothing))?(?<check>, written beside the original to check)?$")]
    private static partial Regex OldDetailRegex();

    // "4.2 s later", "8:54 earlier", positive means the subtitles come later
    internal static string Seconds(int ms)
    {
        var size = Math.Abs(ms);
        var tenths = (size + 50) / 100;
        var whole = (size + 500) / 1000;
        var text = tenths >= 600 ? string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}")
            : size > 0 && tenths == 0 ? "under 0.1 s"
            : (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        return ms < 0 ? text + " earlier" : text + " later";
    }
}
