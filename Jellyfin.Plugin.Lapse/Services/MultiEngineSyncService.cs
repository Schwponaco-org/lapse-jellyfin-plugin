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
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Multi engine sync (experimental).
///
/// Only LAPSE says how sure it is, alass and ffsubsync just hand back a timing. So LAPSE
/// decides when there's doubt and the others give something to compare against. Each
/// answer is written as its own file next to the video and the item is refreshed so they
/// show up as tracks. Nothing gets overwritten until someone keeps one, that's when the
/// output mode setting finally applies.
/// </summary>
public class MultiEngineSyncService
{
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly SubtitleConverter _converter;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MultiEngineSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiEngineSyncService"/> class.
    /// </summary>
    public MultiEngineSyncService(
        EngineRegistry registry,
        EngineRunner runner,
        SubtitleConverter converter,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<MultiEngineSyncService> logger)
    {
        _registry = registry;
        _runner = runner;
        _converter = converter;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether this can be turned on, and why not if it can't.
    /// </summary>
    public MultiEngineAvailability GetAvailability()
    {
        var config = Plugin.Instance!.Configuration;

        var status = new MultiEngineAvailability
        {
            Enabled = config.MultiEngineEnabled,
            LapseIsDefault = string.Equals(config.DefaultEngineId, "lapse", StringComparison.OrdinalIgnoreCase),
            LapseInstalled = IsInstalled("lapse"),
            AlassInstalled = IsInstalled("alass"),
            FfsubsyncInstalled = IsInstalled("ffsubsync"),
            OverwriteAnywayConflict = config.LowConfidenceAction == LowConfidenceAction.OverwriteAnyway,
            PendingCount = config.PendingCandidates.Count
        };

        if (!status.LapseInstalled)
        {
            status.Reason = "LAPSE is not installed. This feature runs on LAPSE's verdict, so it needs LAPSE on disk.";
        }
        else if (!status.LapseIsDefault)
        {
            status.Reason = "LAPSE is not your default engine. Only LAPSE says how sure it is, so only LAPSE can decide when a second opinion is needed.";
        }
        else if (!status.AlassInstalled && !status.FfsubsyncInstalled)
        {
            status.Reason = "Neither alass nor ffsubsync is installed. Install at least one of them to have something to compare LAPSE against.";
        }

        status.Available = status.Reason is null;
        return status;
    }

    /// <summary>
    /// Says whether the other engines should be asked about a finished run.
    /// </summary>
    public bool ShouldBuild(SyncResult result, bool unattended)
    {
        var config = Plugin.Instance?.Configuration;

        // cheap checks first, this runs after every single sync
        if (config is null
            || !config.MultiEngineEnabled
            || (unattended && !config.MultiEngineInBulk)
            || !string.Equals(result.EngineId, "lapse", StringComparison.OrdinalIgnoreCase)
            || !result.LowConfidence
            || result.AlreadyInSync)
        {
            return false;
        }

        // "overwrite anyway" already took LAPSE's word, the original is gone, nothing to choose between
        if (config.LowConfidenceAction == LowConfidenceAction.OverwriteAnyway)
        {
            return false;
        }

        // "nothing" usually means a subtitle for another release, the other engines rarely
        // rescue that, so it's opt in. "unsure" always qualifies.
        if (string.Equals(result.Verdict, "nothing", StringComparison.OrdinalIgnoreCase)
            && config.MultiEngineTrigger != MultiEngineTrigger.UnsureAndNothing)
        {
            return false;
        }

        return GetAvailability().Available;
    }

    /// <summary>
    /// Runs the other engines over the same subtitle and keeps every answer as its own file.
    /// </summary>
    public async Task<SyncCandidateSet?> BuildAsync(
        BaseItem item,
        string referencePath,
        string subtitlePath,
        SyncResult lapseResult,
        CancellationToken cancellationToken = default)
    {
        var fallbacks = GetFallbackEngines();
        if (fallbacks.Count == 0)
        {
            return null;
        }

        // alass and ffsubsync read four formats between them and PGS/VobSub can't be
        // converted to any of them. For those LAPSE's answer is the only one there'll be.
        var readableAsIs = fallbacks.Exists(e => EngineFormats.CanRead(e.Descriptor.Id, subtitlePath));
        if (!readableAsIs && _converter.GetConversionProblem(subtitlePath) is { } problem)
        {
            _logger.LogInformation("Multi engine sync left {Subtitle} alone, only LAPSE can read it: {Problem}", subtitlePath, problem);
            return null;
        }

        // anything left over from an earlier go at this subtitle
        var clearedOldSet = DiscardFor(item.Id, subtitlePath, save: false) > 0;

        var set = new SyncCandidateSet
        {
            ItemId = item.Id,
            ItemName = SyncQueueManager.DescribeItem(item),
            OriginalPath = subtitlePath
        };

        // The others first. If none of them come up with anything there's nothing to
        // compare, and LAPSE's result stays exactly as the normal low confidence handling left it.
        foreach (var engine in fallbacks)
        {
            if (await RunCandidateAsync(engine, referencePath, subtitlePath, cancellationToken).ConfigureAwait(false) is { } candidate)
            {
                set.Candidates.Add(candidate);
            }
        }

        if (set.Candidates.Count == 0)
        {
            _logger.LogInformation("Multi engine sync got nothing usable out of the other engines for {Subtitle}", subtitlePath);

            // the old set's files are gone, write that down or the dashboard keeps offering it
            if (clearedOldSet)
            {
                Plugin.Instance!.SaveConfiguration();
            }

            return null;
        }

        // LAPSE goes first in the list, it's the one with a verdict
        if (await BuildLapseCandidateAsync(referencePath, subtitlePath, lapseResult, cancellationToken).ConfigureAwait(false) is { } lapseCandidate)
        {
            set.Candidates.Insert(0, lapseCandidate);
        }

        // Nothing final was written, the synced subtitle is still where it was and LAPSE's
        // sidecar got renamed into the pile. Mark it so the record, history and dashboard
        // don't report a finished sync with an output file to undo.
        lapseResult.Skipped = true;
        lapseResult.OutputPath = null;
        lapseResult.BackupPath = null;
        lapseResult.CandidateCount = set.Candidates.Count;

        lock (Plugin.ConfigurationLock)
        {
            Plugin.Instance!.Configuration.PendingCandidates.Add(set);
            Plugin.Instance.SaveConfiguration();
        }

        _logger.LogInformation("Multi engine sync left {Count} answers to choose between for {Subtitle}", set.Candidates.Count, subtitlePath);

        // otherwise they sit unseen until the next scan, and the point is to flip between them now
        RequestRefresh(item.Id);
        return set;
    }

    /// <summary>
    /// Gets every set still waiting on a decision, newest first.
    /// </summary>
    public static IReadOnlyList<SyncCandidateSet> GetPending()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Array.Empty<SyncCandidateSet>();
        }

        lock (Plugin.ConfigurationLock)
        {
            return config.PendingCandidates.OrderByDescending(s => s.CreatedUtc).ToList();
        }
    }

    /// <summary>
    /// Gets the sets waiting on a decision for one item.
    /// </summary>
    public static IReadOnlyList<SyncCandidateSet> GetPendingFor(Guid itemId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Array.Empty<SyncCandidateSet>();
        }

        lock (Plugin.ConfigurationLock)
        {
            return config.PendingCandidates.FindAll(s => s.ItemId.Equals(itemId));
        }
    }

    /// <summary>
    /// Keeps one answer and removes the rest. This is where the output mode finally applies:
    /// back up and replace the original, or write a sidecar, same as a normal sync.
    /// </summary>
    public CandidateDecision Keep(Guid itemId, string candidatePath)
    {
        // locked so two Keep presses from two devices can't both move the same file
        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            var set = config.PendingCandidates.Find(s =>
                s.ItemId.Equals(itemId)
                && s.Candidates.Exists(c => string.Equals(c.Path, candidatePath, StringComparison.Ordinal)));

            if (set is null)
            {
                return new CandidateDecision { Message = "There's nothing waiting to be chosen for that subtitle. It may already have been decided." };
            }

            var winner = set.Candidates.Find(c => string.Equals(c.Path, candidatePath, StringComparison.Ordinal))!;

            if (!File.Exists(winner.Path))
            {
                return new CandidateDecision { Message = $"The {winner.EngineName} file has gone from disk, so there's nothing left to keep." };
            }

            var outputMode = config.OutputMode;

            // A candidate can be in another format than the original (alass/ffsubsync input
            // gets converted), and it has to stay that way, we can't write MicroDVD from srt cues.
            var winnerFormat = Path.GetExtension(winner.Path).TrimStart('.');
            var outputFormat = string.Equals(winnerFormat, Path.GetExtension(set.OriginalPath).TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                ? null
                : winnerFormat;

            var destination = EngineRunner.ResolveDestination(set.OriginalPath, outputMode, outputFormat);
            string? backupPath;

            try
            {
                backupPath = EngineRunner.TakeBackup(destination, outputMode);

                if (!string.Equals(winner.Path, destination, StringComparison.Ordinal))
                {
                    File.Move(winner.Path, destination, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not put the chosen subtitle at {Destination}", destination);
                return new CandidateDecision { Message = "Could not write the subtitle you chose: " + ex.Message };
            }

            var removed = set.Candidates.Count(c => !string.Equals(c.Path, winner.Path, StringComparison.Ordinal) && TryDelete(c.Path));
            config.PendingCandidates.Remove(set);

            // normal bookkeeping, so the item counts as synced and Undo works like for any sync
            SyncQueueManager.SaveRecord(
                itemId,
                MovieSyncStatus.Synced,
                null,
                new SyncResult
                {
                    Success = true,
                    Mode = winner.Mode,
                    EngineId = winner.EngineId,
                    OffsetMs = winner.OffsetMs,
                    Slope = winner.Slope,
                    Verdict = winner.Verdict,
                    InputPath = set.OriginalPath,
                    OutputPath = destination,
                    BackupPath = backupPath
                },
                string.Equals(set.OriginalPath, destination, StringComparison.Ordinal)
                    ? new[] { set.OriginalPath }
                    : new[] { set.OriginalPath, destination });

            RequestRefresh(itemId);

            _logger.LogInformation("Kept the {Engine} answer for {Subtitle} and removed {Removed} other(s)", winner.EngineName, set.OriginalPath, removed);

            return new CandidateDecision
            {
                Success = true,
                KeptPath = destination,
                EngineName = winner.EngineName,
                RemovedCount = removed,
                Message = $"Kept the {winner.EngineName} version as {Path.GetFileName(destination)}."
                    + (removed > 0 ? $" Removed {removed} other answer{(removed == 1 ? string.Empty : "s")}." : string.Empty)
                    + (backupPath is null ? string.Empty : " The subtitle it replaced was kept as " + Path.GetFileName(backupPath) + ".")
            };
        }
    }

    /// <summary>
    /// Throws every answer away and leaves the subtitle as it was.
    /// </summary>
    public CandidateDecision Discard(Guid itemId, string? originalPath)
    {
        var removed = DiscardFor(itemId, originalPath, save: true);

        return new CandidateDecision
        {
            Success = true,
            RemovedCount = removed,
            Message = removed == 0
                ? "There was nothing waiting to be chosen."
                : $"Removed {removed} file{(removed == 1 ? string.Empty : "s")} and left the original subtitle as it was."
        };
    }

    /// <summary>
    /// Finds the candidate the caller is watching, so keeping it is one press.
    /// </summary>
    public static SyncCandidate? MatchPlaying(Guid itemId, string? sessionSubtitlePath)
    {
        if (string.IsNullOrEmpty(sessionSubtitlePath))
        {
            return null;
        }

        foreach (var set in GetPendingFor(itemId))
        {
            if (set.Candidates.Find(c => string.Equals(c.Path, sessionSubtitlePath, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Asks Jellyfin to rescan an item so subtitle files written since the last scan show
    /// up as tracks in the player.
    /// </summary>
    public void RequestRefresh(Guid itemId)
    {
        try
        {
            _providerManager.QueueRefresh(itemId, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Could not ask Jellyfin to rescan {Item} for the new subtitle files", itemId);
        }
    }

    // Deletes the candidate files for an item (or one subtitle of it) and forgets the sets.
    private int DiscardFor(Guid itemId, string? originalPath, bool save)
    {
        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            var matches = config.PendingCandidates.FindAll(s =>
                s.ItemId.Equals(itemId)
                && (originalPath is null || string.Equals(s.OriginalPath, originalPath, StringComparison.Ordinal)));

            if (matches.Count == 0)
            {
                return 0;
            }

            var removed = 0;
            foreach (var set in matches)
            {
                removed += set.Candidates.Count(c => TryDelete(c.Path));
                config.PendingCandidates.Remove(set);
            }

            if (save)
            {
                Plugin.Instance.SaveConfiguration();
                RequestRefresh(itemId);
            }

            return removed;
        }
    }

    // LAPSE's answer as a candidate. With the default low confidence setting it's already
    // in a sidecar, so just rename that. With "keep original" nothing was written so ask
    // again, its speech profile is cached from the first run so that's cheap.
    private async Task<SyncCandidate?> BuildLapseCandidateAsync(
        string referencePath,
        string subtitlePath,
        SyncResult lapseResult,
        CancellationToken cancellationToken)
    {
        if (_registry.Find("lapse") is not { } engine)
        {
            return null;
        }

        if (!lapseResult.Success
            || lapseResult.Skipped
            || lapseResult.OutputPath is not { } written
            || !File.Exists(written)
            || string.Equals(written, subtitlePath, StringComparison.Ordinal))
        {
            return await RunCandidateAsync(engine, referencePath, subtitlePath, cancellationToken).ConfigureAwait(false);
        }

        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        var target = BuildCandidatePath(subtitlePath, engine, ResolveCandidateExtension(engine, runtime, subtitlePath));

        try
        {
            if (string.Equals(Path.GetExtension(written), Path.GetExtension(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Move(written, target, overwrite: true);
            }
            else
            {
                await _converter.ConvertAsync(written, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                TryDelete(written);
            }

            return Describe(engine, target, lapseResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not keep LAPSE's own answer as a candidate for {Subtitle}", subtitlePath);
            return null;
        }
    }

    // One engine's answer, written straight to its own candidate file.
    private async Task<SyncCandidate?> RunCandidateAsync(IEngine engine, string referencePath, string subtitlePath, CancellationToken cancellationToken)
    {
        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        var extension = ResolveCandidateExtension(engine, runtime, subtitlePath);
        var target = BuildCandidatePath(subtitlePath, engine, extension);

        try
        {
            // always an extra file, so no backups and no confidence policy
            var result = await _runner.RunAsync(
                    engine,
                    referencePath,
                    subtitlePath,
                    EngineRunner.ResolveDefaultMode(engine),
                    EngineRunner.ResolvePenalty(engine, null),
                    outputMode: OutputMode.SidecarOnly,
                    destinationOverride: target,
                    outputFormat: extension.TrimStart('.'),
                    skipConfidencePolicy: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || !File.Exists(target))
            {
                _logger.LogInformation("{Engine} had nothing to add for {Subtitle}: {Error}", engine.Descriptor.DisplayName, subtitlePath, result.Error ?? "it wrote nothing");
                return null;
            }

            return Describe(engine, target, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not run {Engine} for a candidate on {Subtitle}", engine.Descriptor.DisplayName, subtitlePath);
            return null;
        }
    }

    private static SyncCandidate Describe(IEngine engine, string path, SyncResult result)
    {
        var parts = new List<string>();

        if (result.OffsetMs is { } offset)
        {
            parts.Add(offset == 0 ? "left the timing alone" : string.Create(CultureInfo.InvariantCulture, $"moved it {offset}ms"));
        }

        if (result.Slope is { } slope)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"stretched it {slope * 100:0.###}%"));
        }

        if (result.Verdict is not null)
        {
            parts.Add("LAPSE called this " + result.Verdict);
        }

        return new SyncCandidate
        {
            EngineId = engine.Descriptor.Id,
            EngineName = engine.Descriptor.DisplayName,
            Path = path,
            Mode = result.Mode,
            OffsetMs = result.OffsetMs,
            Slope = result.Slope,
            Verdict = result.Verdict,
            Detail = parts.Count == 0 ? result.EngineOutput : string.Join(", ", parts)
        };
    }

    // Movie.en.srt -> Movie.en.lapse.srt so the engine shows in the player's track list.
    // Doesn't strip a sidecar suffix on purpose: Movie.en.srt and Movie.en.shifted.srt would
    // land on the same candidate name and one set would eat the other's files.
    private static string BuildCandidatePath(string originalPath, IEngine engine, string extension)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(originalPath) + "." + engine.Descriptor.Id;
        var candidate = Path.Combine(directory, name + extension);

        // our leftovers were cleared already, so whatever's there now is someone's real file
        for (var attempt = 2; File.Exists(candidate) && attempt < 100; attempt++)
        {
            candidate = Path.Combine(directory, name + "-" + attempt.ToString(CultureInfo.InvariantCulture) + extension);
        }

        return candidate;
    }

    private static string ResolveCandidateExtension(IEngine engine, EngineRuntimeInfo runtime, string originalPath)
    {
        return (Plugin.Instance?.Configuration.MultiEngineCandidateFormat ?? CandidateFormat.MatchOriginal) switch
        {
            CandidateFormat.Srt => ".srt",
            CandidateFormat.Ass => ".ass",

            // match the original if the engine reads it, otherwise the runner converts to srt
            // on the way in and asking for the original would just convert it back again
            _ => runtime.CanRead(engine.Descriptor.Id, originalPath) ? Path.GetExtension(originalPath) : ".srt"
        };
    }

    private List<IEngine> GetFallbackEngines()
    {
        var config = Plugin.Instance!.Configuration;
        var engines = new List<IEngine>();

        if (config.MultiEngineUseAlass && _registry.Find("alass") is { } alass && IsInstalled(alass))
        {
            engines.Add(alass);
        }

        if (config.MultiEngineUseFfsubsync && _registry.Find("ffsubsync") is { } ffsubsync && IsInstalled(ffsubsync))
        {
            engines.Add(ffsubsync);
        }

        return engines;
    }

    private bool IsInstalled(string engineId)
    {
        return _registry.Find(engineId) is { } engine && IsInstalled(engine);
    }

    private bool IsInstalled(IEngine engine)
    {
        return File.Exists(_runner.ResolvePath(engine));
    }

    // a candidate that won't delete is a stray file, not a reason to fail what someone just chose
    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}

/// <summary>
/// Whether multi engine sync can be used, and what's missing if not.
/// </summary>
public class MultiEngineAvailability
{
    /// <summary>
    /// Gets or sets a value indicating whether the feature is switched on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether everything it needs is in place.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LAPSE is the default engine.
    /// </summary>
    public bool LapseIsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LAPSE is installed.
    /// </summary>
    public bool LapseInstalled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether alass is installed.
    /// </summary>
    public bool AlassInstalled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ffsubsync is installed.
    /// </summary>
    public bool FfsubsyncInstalled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether low confidence is set to "overwrite anyway", which leaves nothing to choose.
    /// </summary>
    public bool OverwriteAnywayConflict { get; set; }

    /// <summary>
    /// Gets or sets how many sets are waiting on a decision.
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// Gets or sets why it can't be used, when it can't.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
/// What came of keeping or discarding a set of answers.
/// </summary>
public class CandidateDecision
{
    /// <summary>
    /// Gets or sets a value indicating whether it worked.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a line to show whoever pressed the button.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets where the kept subtitle ended up.
    /// </summary>
    public string? KeptPath { get; set; }

    /// <summary>
    /// Gets or sets the engine whose answer was kept.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>
    /// Gets or sets how many other answers were removed.
    /// </summary>
    public int RemovedCount { get; set; }
}
