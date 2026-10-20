// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Syncing: one item, an item's tracks against one of them, bulk and series runs, the
/// queue, and one subtitle file against another.
/// </summary>
public partial class LapseController
{
    private const string PictureSubtitleHint = "There's no text in those to line up, they need OCR first (Subtitle Edit can do it).";

    /// <summary>
    /// Syncs one item and waits for the result. A single sync is quick enough to skip the queue.
    /// </summary>
    [HttpPost("Lapse/Sync")]
    public async Task<ActionResult<SyncResult>> Sync([FromBody] SyncRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A sync request body is required");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        string? fetchError = null;

        // Nothing usable (PGS only counts as nothing). Try OpenSubtitles first if that's on.
        if (!subtitles.Exists(s => s.Supported) && Plugin.Instance!.Configuration.OpenSubtitlesEnabled)
        {
            var fetched = await _openSubtitles.TryFetchAsync(item, HttpContext.RequestAborted).ConfigureAwait(false);

            if (fetched.Path is { } fetchedPath)
            {
                subtitles = _subtitleLocator.GetExternalSubtitles(item);

                // jellyfin may not have rescanned yet, so add it by hand if the locator missed it
                if (!subtitles.Exists(s => string.Equals(s.Path, fetchedPath, StringComparison.Ordinal)))
                {
                    subtitles.Add(new SubtitleOption
                    {
                        Path = fetchedPath,
                        DisplayName = Path.GetFileName(fetchedPath),
                        Format = SubtitleFormats.GetName(fetchedPath),
                        Supported = true,
                        TextBased = SubtitleFormats.IsTextBased(fetchedPath)
                    });
                }

                if (string.IsNullOrWhiteSpace(request.SubtitlePath))
                {
                    request.SubtitlePath = fetchedPath;
                }

                _multiEngine.RequestRefresh(item.Id);
            }
            else
            {
                fetchError = fetched.Error;
            }
        }

        if (subtitles.Count == 0)
        {
            return BadRequest(fetchError is null
                ? "This item has no subtitle to sync"
                : "This item has no subtitle, and fetching one didn't work: " + fetchError);
        }

        var usable = subtitles.FindAll(s => s.Supported);
        if (usable.Count == 0)
        {
            return BadRequest("The only subtitles on this item are picture based (PGS or VobSub). " + PictureSubtitleHint);
        }

        SubtitleOption picked;
        if (string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            if (usable.Count > 1)
            {
                return BadRequest("This item has more than one subtitle, pick one with subtitlePath");
            }

            picked = usable[0];
        }
        else
        {
            // only ever the item's own subtitle, with our copy of the path
            var match = subtitles.Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));
            if (match is null)
            {
                return BadRequest("That subtitle doesn't belong to this item");
            }

            if (!match.Supported)
            {
                return BadRequest("That's a picture based subtitle (PGS or VobSub). " + PictureSubtitleHint);
            }

            picked = match;
        }

        var (subtitlePath, resolveError) = await _extractor.ResolveAsync(item, picked, HttpContext.RequestAborted).ConfigureAwait(false);
        if (subtitlePath is null)
        {
            return BadRequest(resolveError ?? "Could not get that subtitle out of the video file.");
        }

        var engine = _registry.Resolve(request.EngineId);
        var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

        // the UI greys these out but don't trust it
        if (!SupportsMode(engine, mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {mode} alignment");
        }

        string? outputFormat = null;
        if (request.OutputFormat is not null
            && !SubtitleFormats.TryNormalizeOutputFormat(request.OutputFormat, out outputFormat))
        {
            return BadRequest("The output format has to be srt, vtt, ass or ssa");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = await _runner
            .RunAsync(engine, item.Path, subtitlePath, mode, penalty, request.OutputMode, outputFormat: outputFormat)
            .ConfigureAwait(false);

        // LAPSE unsure and multi engine on: get the other engines' answers to compare in the player
        if (_multiEngine.ShouldBuild(result, unattended: false))
        {
            var set = await _multiEngine
                .BuildAsync(item, item.Path, subtitlePath, result, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (set is not null)
            {
                result.CandidateCount = set.Candidates.Count;
            }
        }

        SaveSyncRecord(request.ItemId, result, subtitlePath);
        return result;
    }

    // What every single subtitle sync writes down afterwards, so Sync, Convert+sync and the
    // pipeline all agree. A deliberate skip isn't a success on disk or an engine failure,
    // so it goes back to pending with the reason.
    private static void SaveSyncRecord(Guid itemId, SyncResult result, string subtitlePath)
    {
        SaveSyncRecord(itemId, result, SyncedPaths(result, subtitlePath));
    }

    private static void SaveSyncRecord(Guid itemId, SyncResult result, IEnumerable<string> syncedPaths)
    {
        var status = !result.Success ? MovieSyncStatus.Failed
            : result.CountsAsSynced ? MovieSyncStatus.Synced
            : MovieSyncStatus.Pending;

        var message = !result.Success ? result.Error
            : result.CountsAsSynced && !result.AlreadyInSync ? null
            : SyncQueueManager.DescribeSkip(result);

        SyncQueueManager.SaveRecord(
            itemId,
            status,
            message,
            result,
            result.CountsAsSynced ? syncedPaths : null);
    }

    // The synced subtitle plus the new file written beside it, if there is one.
    private static List<string> SyncedPaths(SyncResult result, string subtitlePath)
    {
        var paths = new List<string> { subtitlePath };

        if (!string.IsNullOrEmpty(result.OutputPath) && !string.Equals(result.OutputPath, subtitlePath, StringComparison.Ordinal))
        {
            paths.Add(result.OutputPath);
        }

        return paths;
    }

    /// <summary>
    /// Syncs an item's other subtitles against one of them. No audio decoding, so it's
    /// faster and more accurate than syncing each to the video, as long as the reference is right.
    /// </summary>
    [HttpPost("Lapse/SyncAllSubtitles")]
    public async Task<ActionResult<MultiSubtitleSyncResult>> SyncAllSubtitles(
        [FromBody] MultiSubtitleSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        var reference = subtitles.Find(s => string.Equals(s.Path, request.ReferencePath, StringComparison.Ordinal));
        if (reference is null)
        {
            return BadRequest("That reference subtitle doesn't belong to this item");
        }

        // a PGS track can still be a reference (timing is all we need) if the engine reads it
        if (!reference.Supported)
        {
            return BadRequest("The engine can't read that subtitle, so it can't line anything up against it. Picture based tracks (PGS, VobSub) need an engine that reads them.");
        }

        var others = subtitles.FindAll(s => s.Supported && !string.Equals(s.Path, reference.Path, StringComparison.Ordinal));

        if (request.SubtitlePaths is { Count: > 0 })
        {
            // picker sent specific tracks, only do those
            var wanted = new HashSet<string>(request.SubtitlePaths, StringComparer.Ordinal);
            others.RemoveAll(s => !wanted.Contains(s.Path));

            if (others.Count == 0)
            {
                return BadRequest("None of the subtitles you picked belong to this item, or the only one you picked is the reference itself");
            }
        }
        else
        {
            // sidecars get rewritten with their original, don't sync them twice
            var allPaths = others.ConvertAll(s => s.Path);
            others.RemoveAll(s => EngineRunner.IsSidecarOfAnother(s.Path, allPaths));

            if (others.Count == 0)
            {
                return BadRequest("This item only has the one usable subtitle, so there's nothing to line up against it");
            }
        }

        var engine = _registry.Resolve(request.EngineId);
        var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

        if (!SupportsMode(engine, mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {mode} alignment");
        }

        var (referencePath, referenceError) = await _extractor.ResolveAsync(item, reference, cancellationToken).ConfigureAwait(false);
        if (referencePath is null)
        {
            return BadRequest(referenceError ?? "Could not get the reference subtitle out of the video file.");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = new MultiSubtitleSyncResult { ReferencePath = referencePath };

        // Several runs against the same reference is what batch mode is for. One at a time
        // on purpose, running a few of these at once makes the whole server stutter.
        using var batch = _runner.CreateBatchSession();

        foreach (var subtitle in others)
        {
            var (subtitlePath, subtitleError) = await _extractor.ResolveAsync(item, subtitle, cancellationToken).ConfigureAwait(false);

            var syncResult = subtitlePath is null
                ? new SyncResult
                {
                    Success = false,
                    Mode = mode,
                    EngineId = engine.Descriptor.Id,
                    Error = subtitleError ?? "Could not get that subtitle out of the video file."
                }
                : await _runner
                    .RunAsync(engine, referencePath, subtitlePath, mode, penalty, request.OutputMode, batch: batch, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            if (syncResult.CountsAsSynced)
            {
                result.SucceededCount++;
            }

            result.Results.Add(new SubtitleSyncOutcome
            {
                Path = subtitlePath ?? subtitle.Path,
                DisplayName = subtitle.DisplayName,
                Result = syncResult
            });
        }

        // the reference is right by definition, so it counts as synced too
        var writtenPaths = result.Results
            .Where(o => o.Result?.CountsAsSynced == true)
            .SelectMany(o => SyncedPaths(o.Result!, o.Path))
            .Append(referencePath)
            .ToList();

        var total = result.Results.Count;
        var failed = result.Results.Count(o => o.Result?.Success != true);
        var doubted = total - failed - result.SucceededCount;

        var status = failed > 0 ? MovieSyncStatus.Failed
            : doubted > 0 ? MovieSyncStatus.Pending
            : MovieSyncStatus.Synced;

        var detail = failed > 0 ? $"{failed} of {total} subtitles failed to sync against the reference"
            : doubted > 0 ? $"LAPSE wasn't sure about {doubted} of {total} subtitles, so those weren't counted as synced"
            : null;

        SyncQueueManager.SaveRecord(request.ItemId, status, detail, result.Results.LastOrDefault()?.Result, writtenPaths);

        return result;
    }

    /// <summary>
    /// Starts a background sync of every enabled library, or one library/folder.
    /// </summary>
    [HttpPost("Lapse/BulkSync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult BulkSync([FromBody] BulkSyncRequest request)
    {
        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var started = request.Scope == BulkSyncScope.Folder && request.FolderId.HasValue
            ? _queueManager.EnqueueFolder(request.FolderId.Value)
            : _queueManager.EnqueueLibrary();

        return started ? Accepted() : Conflict("A sync job is already running, or there were no eligible items to sync");
    }

    /// <summary>
    /// Gets the queue progress for the dashboard and the context menu's "X / Y" readout.
    /// </summary>
    [HttpGet("Lapse/Queue")]
    public ActionResult<QueueSnapshot> GetQueue([FromQuery] bool includeItems = false)
    {
        // item names from every library, so not for everyone
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        return _queueManager.GetSnapshot(includeItems);
    }

    /// <summary>
    /// Stops the running job. Anyone who can start one from the context menu can stop it,
    /// a series sync started by mistake shouldn't need an admin.
    /// </summary>
    [HttpPost("Lapse/Queue/Cancel")]
    public ActionResult CancelQueue()
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        var dropped = _queueManager.Cancel();

        return dropped is null
            ? Conflict("There is no sync job running")
            : Ok(new { Dropped = dropped.Value });
    }

    /// <summary>
    /// Gets the tracks found across a series or season, for the "sync all to reference" picker.
    /// </summary>
    [HttpGet("Lapse/Series/{itemId}/ReferenceOptions")]
    public ActionResult<List<ReferenceOption>> GetSeriesReferenceOptions([FromRoute] Guid itemId)
    {
        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        if (!SeriesSyncService.IsSeriesOrSeason(item))
        {
            return BadRequest("That item isn't a series or a season");
        }

        return _seriesSyncService.GetReferenceOptions(item);
    }

    /// <summary>
    /// Queues every episode under a series or season. Progress comes from GET Lapse/Queue.
    /// </summary>
    [HttpPost("Lapse/Series/Sync")]
    public ActionResult SyncSeries([FromBody] SeriesSyncRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        if (!SeriesSyncService.IsSeriesOrSeason(item))
        {
            return BadRequest("That item isn't a series or a season");
        }

        var episodes = _seriesSyncService.GetEpisodes(item).FindAll(_libraryService.IsEligible);
        if (episodes.Count == 0)
        {
            return Conflict("No episodes here are eligible for syncing. Check the library is turned on and nothing is skipped");
        }

        var referenceKey = Blank(request.ReferenceKey);

        return _queueManager.EnqueueItems(episodes, item.Name ?? "Series", "episode", referenceKey)
            ? Accepted(new { Queued = episodes.Count })
            : Conflict("A sync job is already running");
    }

    /// <summary>
    /// Lists the series in a library for the season picker.
    /// </summary>
    [HttpGet("Lapse/Libraries/{libraryId}/Series")]
    public ActionResult<List<FolderEntry>> GetSeriesInLibrary([FromRoute] Guid libraryId)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        return _seriesSyncService.GetSeries(libraryId)
            .Where(series => GetVisibleItem(series.Id) is not null)
            .Select(series => new FolderEntry { ItemId = series.Id, Name = series.Name ?? "Unknown" })
            .ToList();
    }

    /// <summary>
    /// Lists the seasons of a series for the season picker.
    /// </summary>
    [HttpGet("Lapse/Series/{itemId}/Seasons")]
    public ActionResult<List<FolderEntry>> GetSeasons([FromRoute] Guid itemId)
    {
        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return _seriesSyncService.GetSeasons(item)
            .Select(season => new FolderEntry { ItemId = season.Id, Name = season.Name ?? "Unknown" })
            .ToList();
    }

    /// <summary>
    /// Queues every episode in a library.
    /// </summary>
    [HttpPost("Lapse/Libraries/{libraryId}/SyncAllSeries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SyncAllSeries([FromRoute] Guid libraryId)
    {
        // GetItems already drops virtual, skipped and ignored items, so only the library switch is left to check
        var episodes = Plugin.Instance!.Configuration.IsLibraryEnabled(libraryId)
            ? _libraryService.GetItems(libraryId).FindAll(i => !string.IsNullOrEmpty(i.Path))
            : new List<MediaBrowser.Controller.Entities.BaseItem>();

        if (episodes.Count == 0)
        {
            return Conflict("Nothing in that library is eligible for syncing");
        }

        var name = _libraryService.GetLibraries().Find(l => l.ItemId == libraryId)?.Name ?? "Library";

        return _queueManager.EnqueueItems(episodes, name, "episode")
            ? Accepted(new { Queued = episodes.Count })
            : Conflict("A sync job is already running");
    }

    /// <summary>
    /// Marks or unmarks an item or folder as skipped.
    /// </summary>
    [HttpPost("Lapse/Skip")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Skip([FromBody] SkipRequest request)
    {
        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            if (!request.Skip)
            {
                config.SkippedItemIds.Remove(request.ItemId);
            }
            else if (!config.SkippedItemIds.Contains(request.ItemId))
            {
                config.SkippedItemIds.Add(request.ItemId);
            }

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    /// <summary>
    /// Syncs one subtitle file against another. Admin only since the paths can be anywhere.
    /// </summary>
    [HttpPost("Lapse/SyncSubtitles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SyncResult>> SyncSubtitles([FromBody] SubtitleSyncRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ReferencePath) || string.IsNullOrWhiteSpace(request.InputPath))
        {
            return BadRequest("Both a reference and an input subtitle path are required");
        }

        // no library item to check against here, but at least make sure a typo can't
        // turn this into "overwrite that random file"
        if (!SubtitleFormats.IsSubtitle(request.ReferencePath) || !SubtitleFormats.IsSubtitle(request.InputPath))
        {
            return BadRequest("Both paths need to be subtitle files");
        }

        if (!System.IO.File.Exists(request.ReferencePath) || !System.IO.File.Exists(request.InputPath))
        {
            return BadRequest("One of those subtitle files doesn't exist");
        }

        var outputPath = Blank(request.OutputPath);

        // a bare file name goes in the configured placement folder
        if (outputPath is not null && string.IsNullOrEmpty(Path.GetDirectoryName(outputPath)))
        {
            outputPath = Path.Combine(
                ResolveSubToSubFolder(request.ReferencePath, request.InputPath, request.Placement),
                outputPath);
        }

        if (outputPath is not null)
        {
            if (!SubtitleFormats.IsSubtitle(outputPath))
            {
                return BadRequest("The output file needs a subtitle extension");
            }

            if (string.Equals(outputPath, request.ReferencePath, StringComparison.Ordinal))
            {
                return BadRequest("The output can't be the reference subtitle, that would overwrite what's being matched against");
            }

            var outputFolder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
            {
                return BadRequest("That output folder doesn't exist");
            }
        }

        var engine = _registry.Resolve(request.EngineId);
        var penalty = EngineRunner.ResolvePenalty(engine, null);

        // LAPSE's auto mode handles a subtitle reference just like audio (drift, re-cuts),
        // the others only get a plain offset here
        var mode = string.Equals(engine.Descriptor.Id, "lapse", StringComparison.OrdinalIgnoreCase)
            ? EngineRunner.ResolveDefaultMode(engine)
            : SyncMode.Standard;

        return await _runner
            .RunAsync(engine, request.ReferencePath, request.InputPath, mode, penalty, request.OutputMode, outputPath)
            .ConfigureAwait(false);
    }

    // Beside the reference by default, that's the file already sitting next to its video so
    // Jellyfin picks the result up as another track. Input folder leaves it where it was,
    // custom folder keeps it out of the library.
    private static string ResolveSubToSubFolder(string referencePath, string inputPath, SubToSubPlacement? requested)
    {
        var config = Plugin.Instance!.Configuration;
        var placement = requested ?? config.SubToSubPlacement;

        if (placement == SubToSubPlacement.CustomFolder
            && !string.IsNullOrWhiteSpace(config.SubToSubCustomFolder)
            && Directory.Exists(config.SubToSubCustomFolder))
        {
            return config.SubToSubCustomFolder;
        }

        var source = placement == SubToSubPlacement.InputFolder ? inputPath : referencePath;
        return Path.GetDirectoryName(source) ?? Path.GetDirectoryName(inputPath) ?? string.Empty;
    }

    private static bool SupportsMode(IEngine engine, SyncMode mode)
    {
        var capabilities = engine.Descriptor.Capabilities;
        return mode switch
        {
            SyncMode.Auto => capabilities.SupportsAuto,
            SyncMode.Ols => capabilities.SupportsOls,
            SyncMode.Split => capabilities.SupportsSplit,
            _ => capabilities.SupportsStandard
        };
    }
}
