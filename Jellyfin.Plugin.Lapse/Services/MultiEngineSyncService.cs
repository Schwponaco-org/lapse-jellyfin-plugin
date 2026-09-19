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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Multi engine sync. Experimental.
///
/// LAPSE is the only engine that says how sure it is of its own answer. alass and
/// ffsubsync hand back a timing with nothing attached to say whether it is any good, so
/// there is no way to act on their results automatically. That asymmetry is what this is
/// built on: LAPSE decides when there is doubt, and the other engines are there to give
/// something to compare against when there is.
///
/// When LAPSE is not sure, each engine's answer is written as its own subtitle file next
/// to the video, and the item is refreshed so Jellyfin lists them as extra tracks. Nothing
/// is overwritten while that is going on. The subtitle that was synced stays exactly where
/// it was until somebody keeps one of the answers, which is the point where the file
/// output setting finally applies.
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
    /// <param name="registry">The engines we know about.</param>
    /// <param name="runner">Runs them.</param>
    /// <param name="converter">Writes a candidate out in another format when the chosen
    /// candidate format isn't the one the engine produced.</param>
    /// <param name="providerManager">Used to ask Jellyfin to pick the new files up.</param>
    /// <param name="fileSystem">Needed to build the refresh's directory service.</param>
    /// <param name="logger">Logger.</param>
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
    /// Gets what the dashboard needs to say whether this can be turned on, and why not
    /// when it can't.
    /// </summary>
    /// <returns>The current state.</returns>
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
    /// Says whether a finished run should have the other engines asked about it as well.
    /// </summary>
    /// <param name="result">What LAPSE came back with.</param>
    /// <param name="unattended">True for a bulk, scheduled or webhook run.</param>
    /// <returns>True if candidates are worth building.</returns>
    public bool ShouldBuild(SyncResult result, bool unattended)
    {
        var config = Plugin.Instance?.Configuration;

        if (config is null || !config.MultiEngineEnabled || !GetAvailability().Available)
        {
            return false;
        }

        if (unattended && !config.MultiEngineInBulk)
        {
            return false;
        }

        // Only ever off the back of a LAPSE run, since the verdict is what triggers this.
        if (!string.Equals(result.EngineId, "lapse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "Overwrite anyway" means the admin has already said to take LAPSE's word for it
        // whatever the score. The original is gone by the time we get here, so there is
        // nothing left to protect and nothing to choose between.
        if (config.LowConfidenceAction == LowConfidenceAction.OverwriteAnyway)
        {
            return false;
        }

        if (!result.LowConfidence || result.AlreadyInSync)
        {
            return false;
        }

        // "unsure" means LAPSE found an answer it half believes. "nothing" means the audio
        // didn't back it up at all, which usually means the subtitle is for another
        // release, and the other engines rarely rescue that - so it's opt in.
        if (string.Equals(result.Verdict, "nothing", StringComparison.OrdinalIgnoreCase))
        {
            return config.MultiEngineTrigger == MultiEngineTrigger.UnsureAndNothing;
        }

        return true;
    }

    /// <summary>
    /// Runs the other engines over the same subtitle and keeps every answer as a file of
    /// its own, so they can be compared in the player.
    /// </summary>
    /// <param name="item">The item being synced.</param>
    /// <param name="referencePath">What the engines line up against, usually the video.</param>
    /// <param name="subtitlePath">The subtitle that was synced, still untouched on disk.</param>
    /// <param name="lapseResult">What LAPSE came back with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The set that was built, or null when there was nothing worth keeping.</returns>
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

        // alass and ffsubsync read four formats between them. Anything else has to be
        // converted before they can say anything, and PGS and VobSub are pictures of text
        // rather than text, so they can't be converted into anything either engine reads.
        // For those files LAPSE's answer is the only one there will ever be.
        var readableAsIs = fallbacks.Exists(e => EngineFormats.CanRead(e.Descriptor.Id, subtitlePath));

        if (!readableAsIs && _converter.GetConversionProblem(subtitlePath) is { } problem)
        {
            _logger.LogInformation(
                "Multi engine sync left {Subtitle} alone, only LAPSE can read it: {Problem}",
                subtitlePath,
                problem);
            return null;
        }

        // Anything still sitting around from an earlier go at this same subtitle.
        var clearedOldSet = DiscardFor(item.Id, subtitlePath, save: false) > 0;

        var set = new SyncCandidateSet
        {
            ItemId = item.Id,
            ItemName = SyncQueueManager.DescribeItem(item),
            OriginalPath = subtitlePath
        };

        // The other engines run first on purpose. If none of them produces anything there
        // is nothing to compare against, and LAPSE's own result is left exactly as the
        // normal low-confidence handling put it.
        foreach (var engine in fallbacks)
        {
            var candidate = await RunCandidateAsync(engine, referencePath, subtitlePath, cancellationToken)
                .ConfigureAwait(false);

            if (candidate is not null)
            {
                set.Candidates.Add(candidate);
            }
        }

        if (set.Candidates.Count == 0)
        {
            _logger.LogInformation(
                "Multi engine sync got nothing usable out of the other engines for {Subtitle}",
                subtitlePath);

            // An earlier set was cleared off disk on the way in here. Nothing replaced it,
            // so the config has to be written back or it would still list files that have
            // gone, and the dashboard would offer a decision about nothing.
            if (clearedOldSet)
            {
                Plugin.Instance!.SaveConfiguration();
            }

            return null;
        }

        var lapseCandidate = await BuildLapseCandidateAsync(referencePath, subtitlePath, lapseResult, cancellationToken)
            .ConfigureAwait(false);

        if (lapseCandidate is not null)
        {
            // LAPSE first in the list: it's the one with a verdict attached, so it's the
            // one worth looking at first.
            set.Candidates.Insert(0, lapseCandidate);
        }

        // Nothing final has been written. The subtitle that was synced is still sitting
        // where it was, and these files are waiting to be compared. Saying so on the result
        // is what keeps the record, the history and the dashboard from reporting a finished
        // sync: LAPSE's sidecar has just been renamed into the pile of answers, so there is
        // no output file left to undo either.
        lapseResult.Skipped = true;
        lapseResult.OutputPath = null;
        lapseResult.BackupPath = null;
        lapseResult.CandidateCount = set.Candidates.Count;

        var config = Plugin.Instance!.Configuration;
        config.PendingCandidates.Add(set);
        Plugin.Instance.SaveConfiguration();

        _logger.LogInformation(
            "Multi engine sync left {Count} answers to choose between for {Subtitle}",
            set.Candidates.Count,
            subtitlePath);

        // Without this the new files sit there unseen until the next library scan, and the
        // whole point is to switch between them in the player straight away.
        RequestRefresh(item.Id);

        return set;
    }

    /// <summary>
    /// Gets every set still waiting on a decision.
    /// </summary>
    /// <returns>The pending sets, newest first.</returns>
    public static IReadOnlyList<SyncCandidateSet> GetPending()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Array.Empty<SyncCandidateSet>();
        }

        return config.PendingCandidates
            .OrderByDescending(s => s.CreatedUtc)
            .ToList();
    }

    /// <summary>
    /// Gets the sets waiting on a decision for one item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>Its pending sets.</returns>
    public static IReadOnlyList<SyncCandidateSet> GetPendingFor(Guid itemId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Array.Empty<SyncCandidateSet>();
        }

        return config.PendingCandidates.FindAll(s => s.ItemId.Equals(itemId));
    }

    /// <summary>
    /// Keeps one answer and removes the rest.
    ///
    /// This is where the file output setting finally applies. Up to now the candidates have
    /// all been extra files and the original has been sitting untouched; keeping one is
    /// what backs the original up and replaces it, or writes a sidecar, exactly as a normal
    /// sync would have done.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="candidatePath">The answer to keep.</param>
    /// <returns>What happened.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The paths are the plugin's own record of files it wrote itself. The request only picks one of them out of that list; nothing from the request reaches the filesystem.")]
    public CandidateDecision Keep(Guid itemId, string candidatePath)
    {
        var config = Plugin.Instance!.Configuration;

        var set = config.PendingCandidates.Find(s =>
            s.ItemId.Equals(itemId)
            && s.Candidates.Exists(c => string.Equals(c.Path, candidatePath, StringComparison.Ordinal)));

        if (set is null)
        {
            return new CandidateDecision
            {
                Message = "There's nothing waiting to be chosen for that subtitle. It may already have been decided."
            };
        }

        var winner = set.Candidates.Find(c => string.Equals(c.Path, candidatePath, StringComparison.Ordinal))!;

        if (!File.Exists(winner.Path))
        {
            return new CandidateDecision
            {
                Message = $"The {winner.EngineName} file has gone from disk, so there's nothing left to keep."
            };
        }

        var outputMode = Plugin.Instance!.Configuration.OutputMode;

        // A candidate can be in a different format from the subtitle it came from, because
        // alass and ffsubsync only read four formats and anything else was converted on the
        // way in. The result then has to keep the format it actually came out in - there is
        // no writing MicroDVD back out of srt cues.
        var winnerFormat = Path.GetExtension(winner.Path).TrimStart('.');
        var originalFormat = Path.GetExtension(set.OriginalPath).TrimStart('.');
        var outputFormat = string.Equals(winnerFormat, originalFormat, StringComparison.OrdinalIgnoreCase)
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
            return new CandidateDecision
            {
                Message = "Could not write the subtitle you chose: " + ex.Message
            };
        }

        var removed = 0;
        foreach (var other in set.Candidates)
        {
            if (!string.Equals(other.Path, winner.Path, StringComparison.Ordinal) && TryDelete(other.Path))
            {
                removed++;
            }
        }

        config.PendingCandidates.Remove(set);

        // Reuses the normal bookkeeping so the item counts as synced and the result turns
        // up in the history, which is what makes Undo work on it like any other sync.
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
            new[] { set.OriginalPath });

        RequestRefresh(itemId);

        _logger.LogInformation(
            "Kept the {Engine} answer for {Subtitle} and removed {Removed} other(s)",
            winner.EngineName,
            set.OriginalPath,
            removed);

        var backupNote = backupPath is null
            ? string.Empty
            : " The subtitle it replaced was kept as " + Path.GetFileName(backupPath) + ".";

        return new CandidateDecision
        {
            Success = true,
            KeptPath = destination,
            EngineName = winner.EngineName,
            RemovedCount = removed,
            Message = $"Kept the {winner.EngineName} version as {Path.GetFileName(destination)}."
                + (removed > 0 ? $" Removed {removed} other answer{(removed == 1 ? string.Empty : "s")}." : string.Empty)
                + backupNote
        };
    }

    /// <summary>
    /// Throws every answer away and leaves the subtitle as it was.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="originalPath">Which subtitle's answers to drop, or null for all of the
    /// item's.</param>
    /// <returns>What happened.</returns>
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
    /// Works out which candidate the person asking is watching right now, so keeping it is
    /// one press rather than a trip through a list of file names.
    /// </summary>
    /// <param name="sessionSubtitlePath">The subtitle file the player reports as playing.</param>
    /// <param name="itemId">The item being played.</param>
    /// <returns>The candidate, or null if what's playing isn't one of them.</returns>
    public static SyncCandidate? MatchPlaying(Guid itemId, string? sessionSubtitlePath)
    {
        if (string.IsNullOrEmpty(sessionSubtitlePath))
        {
            return null;
        }

        foreach (var set in GetPendingFor(itemId))
        {
            var match = set.Candidates.Find(c =>
                string.Equals(c.Path, sessionSubtitlePath, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    // Deletes the files for one item (optionally one subtitle's worth) and forgets the set.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever deletes paths the plugin recorded when it wrote those files itself.")]
    private int DiscardFor(Guid itemId, string? originalPath, bool save)
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
            foreach (var candidate in set.Candidates)
            {
                if (TryDelete(candidate.Path))
                {
                    removed++;
                }
            }

            config.PendingCandidates.Remove(set);
        }

        if (save)
        {
            Plugin.Instance.SaveConfiguration();
            RequestRefresh(itemId);
        }

        return removed;
    }

    // LAPSE's own answer, as a candidate file. Under the default low-confidence setting it
    // has already been written to a sidecar, so that file is simply renamed rather than
    // paying for a second run over the audio. Under "keep the original" nothing was
    // written, so LAPSE is asked again - its speech profile is cached from the first run,
    // which is what makes that affordable.
    private async Task<SyncCandidate?> BuildLapseCandidateAsync(
        string referencePath,
        string subtitlePath,
        SyncResult lapseResult,
        CancellationToken cancellationToken)
    {
        var engine = _registry.Find("lapse");
        if (engine is null)
        {
            return null;
        }

        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        var target = BuildCandidatePath(subtitlePath, engine, ResolveCandidateExtension(engine, runtime, subtitlePath));

        if (lapseResult.Success
            && !lapseResult.Skipped
            && lapseResult.OutputPath is { } written
            && File.Exists(written)
            && !string.Equals(written, subtitlePath, StringComparison.Ordinal))
        {
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

        return await RunCandidateAsync(engine, referencePath, subtitlePath, cancellationToken).ConfigureAwait(false);
    }

    // One engine's answer, written straight to its own candidate file.
    private async Task<SyncCandidate?> RunCandidateAsync(
        IEngine engine,
        string referencePath,
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        var extension = ResolveCandidateExtension(engine, runtime, subtitlePath);
        var target = BuildCandidatePath(subtitlePath, engine, extension);

        try
        {
            var result = await _runner.RunAsync(
                    engine,
                    referencePath,
                    subtitlePath,
                    EngineRunner.ResolveDefaultMode(engine),
                    EngineRunner.ResolvePenalty(engine, null),

                    // A candidate is always an extra file, never a replacement, so the
                    // output mode that would have backed something up doesn't apply here.
                    outputMode: OutputMode.SidecarOnly,
                    destinationOverride: target,
                    outputFormat: extension.TrimStart('.'),
                    skipConfidencePolicy: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || !File.Exists(target))
            {
                _logger.LogInformation(
                    "{Engine} had nothing to add for {Subtitle}: {Error}",
                    engine.Descriptor.DisplayName,
                    subtitlePath,
                    result.Error ?? "it wrote nothing");
                return null;
            }

            return Describe(engine, target, result);
        }
        catch (OperationCanceledException)
        {
            throw;
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
            parts.Add(offset == 0
                ? "left the timing alone"
                : string.Create(CultureInfo.InvariantCulture, $"moved it {offset}ms"));
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

    // Movie.en.srt becomes Movie.en.lapse.srt, so the engine that wrote it is readable in
    // the player's own track list without opening anything.
    //
    // Deliberately not stripping a sidecar suffix the way ResolveDestination does. An item
    // that has been synced before has both Movie.en.srt and Movie.en.shifted.srt sitting in
    // it, and stripping would map both of those onto the same candidate name - so syncing
    // the pair would have one set quietly writing over the other's files, and keeping one
    // answer would delete a file belonging to the other. A longer name is worth not doing
    // that.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Derived from a subtitle path the caller already validated, and only used to pick a name that isn't taken.")]
    private static string BuildCandidatePath(string originalPath, IEngine engine, string extension)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var name = stem + "." + engine.Descriptor.Id;

        var candidate = Path.Combine(directory, name + extension);

        // Our own leftovers were cleared before this ran, so anything sitting at that name
        // now is somebody else's file - a subtitle that happens to be called Movie.en.lapse
        // .srt, most likely from an earlier run of this that was never decided and then got
        // renamed by hand. Writing over it would lose a real subtitle.
        for (var attempt = 2; File.Exists(candidate) && attempt < 100; attempt++)
        {
            candidate = Path.Combine(
                directory,
                name + "-" + attempt.ToString(CultureInfo.InvariantCulture) + extension);
        }

        return candidate;
    }

    // What format this engine's candidate comes out in.
    private static string ResolveCandidateExtension(IEngine engine, EngineRuntimeInfo runtime, string originalPath)
    {
        switch (Plugin.Instance?.Configuration.MultiEngineCandidateFormat ?? CandidateFormat.MatchOriginal)
        {
            case CandidateFormat.Srt:
                return ".srt";

            case CandidateFormat.Ass:
                return ".ass";

            default:
                // Match the original where the engine can actually read it. Where it can't,
                // the runner converts the input to srt on the way in and the answer comes
                // back as srt, so asking for the original format here would only mean
                // converting it a second time on the way out.
                return runtime.CanRead(engine.Descriptor.Id, originalPath)
                    ? Path.GetExtension(originalPath)
                    : ".srt";
        }
    }

    private List<IEngine> GetFallbackEngines()
    {
        var config = Plugin.Instance!.Configuration;
        var engines = new List<IEngine>();

        if (config.MultiEngineUseAlass && _registry.Find("alass") is { } alass && IsInstalled("alass"))
        {
            engines.Add(alass);
        }

        if (config.MultiEngineUseFfsubsync && _registry.Find("ffsubsync") is { } ffsubsync && IsInstalled("ffsubsync"))
        {
            engines.Add(ffsubsync);
        }

        return engines;
    }

    private bool IsInstalled(string engineId)
    {
        var engine = _registry.Find(engineId);
        return engine is not null && File.Exists(_runner.ResolvePath(engine));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever deletes paths the plugin recorded when it wrote those files itself.")]
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
            // A candidate that won't delete is a stray file, not a reason to fail the
            // decision the person just made.
        }

        return false;
    }

    /// <summary>
    /// Asks Jellyfin to look at an item again, so a subtitle file written since the last
    /// scan becomes a track the player will offer. Shared with the in-player panel, which
    /// needs the same thing for a subtitle it just fetched or synced.
    /// </summary>
    /// <param name="itemId">The item to rescan.</param>
    public void RequestRefreshFor(Guid itemId)
    {
        RequestRefresh(itemId);
    }

    // New subtitle files sit unseen until the next library scan, and this feature is built
    // on switching between them in the player straight after the sync.
    private void RequestRefresh(Guid itemId)
    {
        try
        {
            _providerManager.QueueRefresh(
                itemId,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                RefreshPriority.High);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Could not ask Jellyfin to rescan {Item} for the new subtitle files", itemId);
        }
    }
}

/// <summary>
/// Whether multi engine sync can be used, and what's missing when it can't.
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
    /// Gets or sets a value indicating whether the low-confidence setting is "overwrite
    /// anyway", which leaves nothing to choose between.
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
    /// Gets or sets where the subtitle that was kept ended up.
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
