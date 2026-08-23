// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Lapse.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// API endpoints for the LAPSE dashboard and the injected context menu.
///
/// Read-only endpoints just need a logged in user. Anything that changes the plugin
/// itself - settings, engines, library configuration, bulk runs - needs an admin
/// (RequiresElevation). Working on one item's subtitles sits in between: it goes through
/// Jellyfin's own subtitle management permission, so the people already trusted to
/// download subtitles into the library can line them up too, unless an admin has turned
/// that off in the settings.
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class LapseController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly EngineInstaller _installer;
    private readonly EngineCapabilityProbe _probe;
    private readonly EngineUpdater _updater;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly SubtitleShifter _subtitleShifter;
    private readonly SubtitleConverter _converter;
    private readonly SubtitleExtractor _extractor;
    private readonly TranslationService _translationService;
    private readonly SeriesSyncService _seriesSyncService;
    private readonly OpenSubtitlesService _openSubtitles;
    private readonly ArrWebhookService _arrWebhookService;
    private readonly SyncHistoryService _historyService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseController"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up items.</param>
    /// <param name="userManager">Used to check who the caller is.</param>
    /// <param name="libraryService">Libraries and which of their items are eligible.</param>
    /// <param name="queueManager">Runs bulk/background sync jobs.</param>
    /// <param name="registry">The engines we know about.</param>
    /// <param name="runner">Runs single, synchronous engine calls.</param>
    /// <param name="installer">Downloads and installs engines.</param>
    /// <param name="probe">Cleared when an engine is removed, since the file has gone.</param>
    /// <param name="updater">Checks for and applies engine updates.</param>
    /// <param name="subtitleLocator">Finds external subtitles for an item.</param>
    /// <param name="subtitleShifter">Nudges subtitle timings by hand.</param>
    /// <param name="converter">Writes subtitles out in another format.</param>
    /// <param name="extractor">Pulls subtitle tracks out of video files.</param>
    /// <param name="translationService">Translates subtitle files.</param>
    /// <param name="seriesSyncService">Expands a series or season into its episodes.</param>
    /// <param name="openSubtitles">Fetches a subtitle for an item that has none.</param>
    /// <param name="arrWebhookService">Turns Radarr/Sonarr imports into syncs.</param>
    /// <param name="historyService">Puts a sync back the way it was.</param>
    public LapseController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        LibraryService libraryService,
        SyncQueueManager queueManager,
        EngineRegistry registry,
        EngineRunner runner,
        EngineInstaller installer,
        EngineCapabilityProbe probe,
        EngineUpdater updater,
        SubtitleLocator subtitleLocator,
        SubtitleShifter subtitleShifter,
        SubtitleConverter converter,
        SubtitleExtractor extractor,
        TranslationService translationService,
        SeriesSyncService seriesSyncService,
        OpenSubtitlesService openSubtitles,
        ArrWebhookService arrWebhookService,
        SyncHistoryService historyService)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _libraryService = libraryService;
        _queueManager = queueManager;
        _registry = registry;
        _runner = runner;
        _installer = installer;
        _probe = probe;
        _updater = updater;
        _subtitleLocator = subtitleLocator;
        _subtitleShifter = subtitleShifter;
        _converter = converter;
        _extractor = extractor;
        _translationService = translationService;
        _seriesSyncService = seriesSyncService;
        _openSubtitles = openSubtitles;
        _arrWebhookService = arrWebhookService;
        _historyService = historyService;
    }

    // The role name Jellyfin puts in the token for an administrator. Its own constant
    // lives in the server assembly, which plugins don't reference.
    private const string AdministratorRole = "Administrator";

    // Jellyfin's own claim for the signed in user's id. Same source the server's
    // authorization handlers read, and it's what makes the picked-users list work.
    private const string UserIdClaim = "Jellyfin-UserId";

    private const string AccessDeniedMessage =
        "Only administrators can do this. An admin can open it up to other users under Access in the LAPSE settings.";

    /// <summary>
    /// Checks whether the caller may work on an item's subtitles. Administrators always
    /// may; everyone else depends on what an admin chose in the settings.
    /// </summary>
    /// <returns>Null when allowed, otherwise the result to return.</returns>
    private ActionResult? CheckSubtitleAccess()
    {
        if (User.IsInRole(AdministratorRole))
        {
            return null;
        }

        var config = Plugin.Instance!.Configuration;

        switch (config.SubtitleAccess)
        {
            case SubtitleAccessMode.Everyone:
                return null;

            case SubtitleAccessMode.SubtitleManagers:
                return GetCallingUser()?.HasPermission(PermissionKind.EnableSubtitleManagement) == true
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, AccessDeniedMessage);

            case SubtitleAccessMode.SelectedUsers:
                var user = GetCallingUser();
                return user is not null
                    && config.SubtitleAccessUserIds.Exists(id =>
                        Guid.TryParse(id, out var parsed) && parsed.Equals(user.Id))
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, AccessDeniedMessage);

            default:
                return StatusCode(StatusCodes.Status403Forbidden, AccessDeniedMessage);
        }
    }

    // Both ways Jellyfin identifies the caller: the id claim its own handlers use, and
    // the user name, which is there on every token and is unique server wide.
    private User? GetCallingUser()
    {
        var id = User.FindFirst(UserIdClaim)?.Value;
        if (Guid.TryParse(id, out var userId) && !userId.Equals(default))
        {
            var byId = _userManager.GetUserById(userId);
            if (byId is not null)
            {
                return byId;
            }
        }

        var name = User.Identity?.Name;
        return string.IsNullOrEmpty(name) ? null : _userManager.GetUserByName(name);
    }

    // ---------------------------------------------------------------- status and items

    /// <summary>
    /// Gets sync status for every syncable item across the enabled libraries.
    /// </summary>
    /// <returns>One entry per item.</returns>
    [HttpGet("Lapse/Status")]
    public ActionResult<List<ItemStatusEntry>> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var libraryNames = _libraryService.GetLibraries().ToDictionary(l => l.ItemId, l => l.Name);
        var result = new List<ItemStatusEntry>();

        foreach (var (item, libraryId) in _libraryService.GetItemsWithLibrary(includeSkipped: true))
        {
            var record = config.MovieRecords.FirstOrDefault(r => r.ItemId == item.Id);
            var subtitles = _subtitleLocator.GetExternalSubtitles(item);

            // How many of the subtitles this item has right now have actually been
            // synced. Anything the record claims about a file that is no longer there
            // doesn't count, so a replaced subtitle correctly drops back to unsynced.
            var syncedCount = record is null
                ? 0
                : subtitles.Count(s => record.SyncedSubtitles
                    .Any(r => string.Equals(r.Path, s.Path, StringComparison.Ordinal)));

            result.Add(new ItemStatusEntry
            {
                ItemId = item.Id,
                Name = SyncQueueManager.DescribeItem(item),
                ItemType = item.GetBaseItemKind().ToString(),
                LibraryId = libraryId,
                LibraryName = libraryNames.TryGetValue(libraryId, out var name) ? name : null,
                Status = ResolveDisplayStatus(item, record, subtitles.Count, syncedCount),
                LastSyncUtc = record?.LastSyncUtc,
                LastError = record?.LastError,
                SubtitleCount = subtitles.Count,
                SyncedSubtitleCount = syncedCount,
                Path = item.Path
            });
        }

        return result;
    }

    /// <summary>
    /// Gets what the dashboard front page needs that the item list can't tell it: which
    /// engine is active and what has been synced lately.
    ///
    /// Deliberately does not count the library. The dashboard already fetches the full
    /// status list for its own page, and walking every item and stat-ing its subtitles a
    /// second time to produce numbers the browser can add up itself would double the
    /// cost of a page load on a large library for nothing.
    /// </summary>
    /// <returns>The overview.</returns>
    [HttpGet("Lapse/Overview")]
    public ActionResult<DashboardOverview> GetOverview()
    {
        var config = Plugin.Instance!.Configuration;
        var overview = new DashboardOverview();

        foreach (var engine in _registry.All)
        {
            if (System.IO.File.Exists(_runner.ResolvePath(engine)))
            {
                overview.AnyEngineInstalled = true;
                break;
            }
        }

        var active = _registry.GetDefault();
        var activeSettings = config.GetEngineSettings(active.Descriptor.Id);

        overview.ActiveEngineId = active.Descriptor.Id;
        overview.ActiveEngineName = active.Descriptor.DisplayName;
        overview.ActiveEngineVersion = activeSettings.InstalledVersion;
        overview.ActiveEngineMode = EngineRunner.ResolveDefaultMode(active).ToString();
        overview.ActiveEngineReady = System.IO.File.Exists(_runner.ResolvePath(active));

        foreach (var entry in config.History.AsEnumerable().Reverse().Take(15))
        {
            var item = _libraryManager.GetItemById(entry.ItemId);

            overview.Recent.Add(new RecentActivityEntry
            {
                Id = entry.Id,
                ItemId = entry.ItemId,
                Name = item is null ? "(removed from the library)" : SyncQueueManager.DescribeItem(item),
                Status = entry.Status,
                WhenUtc = entry.WhenUtc,
                Detail = entry.Detail,
                OutputPath = entry.OutputPath,
                Reverted = entry.Reverted,
                CanRevert = SyncHistoryService.CanRevert(entry)
            });
        }

        return overview;
    }

    /// <summary>
    /// Undoes one sync: puts the backup back, or removes the file the run added.
    /// </summary>
    /// <param name="id">The history entry.</param>
    /// <returns>A line saying what happened.</returns>
    [HttpPost("Lapse/History/{id}/Revert")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RevertSync([FromRoute] Guid id)
    {
        var outcome = _historyService.Revert(id);
        return outcome is null ? NotFound("No history entry with that id") : Ok(new { Outcome = outcome });
    }

    /// <summary>
    /// Works out what to show against an item.
    ///
    /// This is derived from which subtitle files have actually been synced rather than
    /// from a flag on the record. A flag couldn't tell a fully synced item from one where
    /// a single track out of four was done, and it couldn't tell either of those from a
    /// record left behind by an older install - which is why a fresh install used to show
    /// a whole library as synced without a single sync having been run.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="record">Its sync record, if it has one.</param>
    /// <param name="subtitleCount">How many external subtitles it has now.</param>
    /// <param name="syncedCount">How many of those have been synced.</param>
    /// <returns>The status to show.</returns>
    private static MovieSyncStatus ResolveDisplayStatus(
        BaseItem item,
        MovieSyncRecord? record,
        int subtitleCount,
        int syncedCount)
    {
        // Ignore wins over skip: it's the stronger, standing rule, and showing it as
        // "skipped" would hide the fact that a series-wide rule is what's stopping this.
        if (LibraryService.IsIgnored(item))
        {
            return MovieSyncStatus.Ignored;
        }

        if (SyncQueueManager.IsSkipped(item))
        {
            return MovieSyncStatus.Skipped;
        }

        // A failure is worth surfacing even when an earlier run had got some tracks done.
        if (record?.Status == MovieSyncStatus.Failed && syncedCount < subtitleCount)
        {
            return MovieSyncStatus.Failed;
        }

        if (syncedCount == 0)
        {
            return MovieSyncStatus.Pending;
        }

        return syncedCount >= subtitleCount ? MovieSyncStatus.Synced : MovieSyncStatus.PartiallySynced;
    }

    /// <summary>
    /// Gets the external subtitle files for one item, for the subtitle pickers.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>List of subtitle options.</returns>
    [HttpGet("Lapse/Items/{itemId}/Subtitles")]
    [HttpGet("Lapse/Movies/{itemId}/Subtitles")]
    public ActionResult<List<SubtitleOption>> GetItemSubtitles([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return _subtitleLocator.GetExternalSubtitles(item);
    }

    /// <summary>
    /// Gets the first cue out of one of an item's subtitles, so the shift dialog can show
    /// what an offset would do to a line rather than describing it in the abstract.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="path">The subtitle file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first cue, or 404 if there isn't one.</returns>
    [HttpGet("Lapse/Items/{itemId}/Subtitles/FirstCue")]
    public async Task<ActionResult<SubtitlePreview>> GetFirstCue(
        [FromRoute] Guid itemId,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        var match = FindSubtitle(itemId, path);
        if (match is null)
        {
            return NotFound("That subtitle doesn't belong to this item");
        }

        if (match.IsEmbedded)
        {
            return NotFound("That track is still inside the video file. Sync or convert it once and it becomes a subtitle file this can read.");
        }

        var preview = await SubtitleShifter.ReadFirstCueAsync(match.Path, cancellationToken).ConfigureAwait(false);
        return preview is null ? NotFound("That subtitle has no cues in it") : preview;
    }

    // ---------------------------------------------------------------------- syncing

    /// <summary>
    /// Syncs one item right now and waits for the result. Used by both the quick
    /// "Sync" popup and the advanced dialog, since a single sync is fast enough
    /// not to need the background queue.
    /// </summary>
    /// <param name="request">Which item, which mode, and which subtitle.</param>
    /// <returns>The engine result.</returns>
    [HttpPost("Lapse/Sync")]
    public async Task<ActionResult<SyncResult>> Sync([FromBody] SyncRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        // A body that didn't parse arrives as null, and reading through it is a 500 where
        // the caller deserves to be told what was wrong with the request.
        if (request is null)
        {
            return BadRequest("A sync request body is required");
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        string? fetchError = null;

        // Nothing to line up. If subtitle fetching is switched on, go and get one first
        // rather than turning the sync away.
        if (subtitles.Count == 0 && Plugin.Instance!.Configuration.OpenSubtitlesEnabled)
        {
            var fetched = await _openSubtitles.TryFetchAsync(item).ConfigureAwait(false);

            if (fetched.Path is { } fetchedPath)
            {
                subtitles = _subtitleLocator.GetExternalSubtitles(item);

                // Jellyfin may not have rescanned the folder yet, so the locator can still
                // come back empty even though the file is right there. Use it directly.
                if (subtitles.Count == 0)
                {
                    subtitles = new List<SubtitleOption>
                    {
                        new()
                        {
                            Path = fetchedPath,
                            DisplayName = Path.GetFileName(fetchedPath),
                            Format = SubtitleFormats.GetName(fetchedPath)
                        }
                    };
                }
            }
            else
            {
                // Whatever went wrong goes back to whoever pressed Sync. Quietly returning
                // "no subtitle" here was the reason fetching looked like it did nothing.
                fetchError = fetched.Error;
            }
        }

        if (subtitles.Count == 0)
        {
            return BadRequest(fetchError is null
                ? "This item has no subtitle to sync"
                : "This item has no subtitle, and fetching one didn't work: " + fetchError);
        }

        // Picture based tracks can't be lined up by anything here, so they don't count
        // towards "which one did you mean".
        var usable = subtitles.FindAll(s => s.Supported);
        if (usable.Count == 0)
        {
            return BadRequest("The only subtitles on this item are picture based (PGS or VobSub). There's no text in those to line up - they need OCR first, with something like Subtitle Edit.");
        }

        SubtitleOption picked;
        if (string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            if (usable.Count > 1)
            {
                return BadRequest("This item has more than one subtitle - pick one with subtitlePath");
            }

            picked = usable[0];
        }
        else
        {
            // Only ever write to a subtitle the library actually knows about for this
            // item, and use the library's own copy of the path rather than the one from
            // the request, so this can't be talked into rewriting some other file.
            var match = subtitles.Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));
            if (match is null)
            {
                return BadRequest("That subtitle doesn't belong to this item");
            }

            if (!match.Supported)
            {
                return BadRequest("That's a picture based subtitle (PGS or VobSub). There's no text in it to line up - it needs OCR first, with something like Subtitle Edit.");
            }

            picked = match;
        }

        var (subtitlePath, resolveError) = await ResolveToFileAsync(item, picked, HttpContext.RequestAborted).ConfigureAwait(false);
        if (subtitlePath is null)
        {
            return BadRequest(resolveError ?? "Could not get that subtitle out of the video file.");
        }

        var engine = _registry.Resolve(request.EngineId);
        var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

        // Don't let a caller ask for something the chosen engine can't do. The dashboard
        // already greys these out, but the API shouldn't rely on the UI behaving.
        if (!SupportsMode(engine, mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {mode} alignment");
        }

        if (request.OutputFormat is not null
            && !SubtitleFormats.TryNormalizeOutputFormat(request.OutputFormat, out _))
        {
            return BadRequest("The output format has to be srt, vtt, ass or ssa");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = await _runner
            .RunAsync(engine, item.Path, subtitlePath, mode, penalty, request.OutputMode, outputFormat: request.OutputFormat)
            .ConfigureAwait(false);

        SyncQueueManager.SaveRecord(
            request.ItemId,
            ResolveRecordStatus(result),
            result.Success && result.Skipped ? SyncQueueManager.DescribeSkip(result) : result.Error,
            result,
            result.Success && !result.Skipped ? new[] { subtitlePath } : null);

        return result;
    }

    // A result the plugin deliberately didn't write isn't a success on disk and isn't an
    // engine failure either, so it goes back to pending with the reason attached.
    private static MovieSyncStatus ResolveRecordStatus(SyncResult result)
    {
        if (!result.Success)
        {
            return MovieSyncStatus.Failed;
        }

        return result.Skipped ? MovieSyncStatus.Pending : MovieSyncStatus.Synced;
    }

    /// <summary>
    /// Syncs every other subtitle on an item against one of them. Lining a subtitle up
    /// against another subtitle skips the audio decoding entirely, so this is both faster
    /// and more accurate than syncing each track to the video separately - as long as one
    /// of the tracks is known to be right.
    /// </summary>
    /// <param name="request">The item and which of its subtitles is the reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per non-reference subtitle.</returns>
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

        var item = _libraryManager.GetItemById(request.ItemId);
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

        // Only ever line something up against a reference that's actually correct.
        // Picture based tracks have no text to compare, so they can't be a reference.
        if (!reference.Supported)
        {
            return BadRequest("That's a picture based subtitle (PGS or VobSub), so it can't be used as a reference - there's no text in it.");
        }

        var others = subtitles.Where(s => !string.Equals(s.Path, reference.Path, StringComparison.Ordinal) && s.Supported).ToList();
        if (others.Count == 0)
        {
            return BadRequest("This item only has the one usable subtitle, so there's nothing to line up against it");
        }

        var engine = _registry.Resolve(request.EngineId);
        var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

        if (!SupportsMode(engine, mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {mode} alignment");
        }

        // This is a deliberate action on one item, same as the plain Sync button, so an
        // embedded reference or track is worth pulling out rather than turning away - the
        // caller picked this item on purpose.
        var (referencePath, referenceError) = await ResolveToFileAsync(item, reference, cancellationToken).ConfigureAwait(false);
        if (referencePath is null)
        {
            return BadRequest(referenceError ?? "Could not get the reference subtitle out of the video file.");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = new MultiSubtitleSyncResult { ReferencePath = referencePath };

        // Sequential on purpose: these engines are CPU heavy and running a handful of them
        // at once on a media server tends to make everything else stutter.
        foreach (var subtitle in others)
        {
            var (subtitlePath, subtitleError) = await ResolveToFileAsync(item, subtitle, cancellationToken).ConfigureAwait(false);

            SyncResult syncResult;
            if (subtitlePath is null)
            {
                syncResult = new SyncResult
                {
                    Success = false,
                    Mode = mode,
                    EngineId = engine.Descriptor.Id,
                    Error = subtitleError ?? "Could not get that subtitle out of the video file."
                };
            }
            else
            {
                syncResult = await _runner
                    .RunAsync(engine, referencePath, subtitlePath, mode, penalty, request.OutputMode, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (syncResult.Success)
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

        // The reference itself is correct by definition - that's why it was picked - so it
        // counts as synced alongside everything that was lined up against it.
        var writtenPaths = result.Results
            .Where(o => o.Result is { Success: true, Skipped: false })
            .Select(o => o.Path)
            .Append(referencePath)
            .ToList();

        SyncQueueManager.SaveRecord(
            request.ItemId,
            result.SucceededCount == others.Count ? MovieSyncStatus.Synced : MovieSyncStatus.Failed,
            result.SucceededCount == others.Count ? null : "Some subtitles failed to sync against the reference",
            result.Results.LastOrDefault()?.Result,
            writtenPaths);

        return result;
    }

    /// <summary>
    /// Starts a background job to sync everything in the enabled libraries, or just one
    /// library or folder.
    /// </summary>
    /// <param name="request">Scope of the job.</param>
    /// <returns>202 if the job started, 409 if one was already running or there was nothing to do.</returns>
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

        if (!started)
        {
            return Conflict("A sync job is already running, or there were no eligible items to sync");
        }

        return Accepted();
    }

    /// <summary>
    /// Gets the current bulk sync queue progress, for the dashboard's progress bar and
    /// the context menu's "X / Y episodes processed" readout.
    /// </summary>
    /// <returns>Queue snapshot.</returns>
    [HttpGet("Lapse/Queue")]
    public ActionResult<QueueSnapshot> GetQueue()
    {
        return _queueManager.GetSnapshot();
    }

    /// <summary>
    /// Gets the subtitle tracks that exist across the episodes of a series or season, for
    /// the "sync all to reference" picker. A single episode's reference is one file; a
    /// series' reference has to be a track that means the same thing on every episode.
    /// </summary>
    /// <param name="itemId">The series or season.</param>
    /// <returns>One option per track, most widely available first.</returns>
    [HttpGet("Lapse/Series/{itemId}/ReferenceOptions")]
    public ActionResult<List<ReferenceOption>> GetSeriesReferenceOptions([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
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
    /// Starts a background job syncing every episode under a series or season. Runs
    /// through the same queue as a bulk sync, so progress comes back from GET Lapse/Queue
    /// rather than this call sitting there until a whole show is done.
    /// </summary>
    /// <param name="request">The series or season, and optionally a reference track.</param>
    /// <returns>How many episodes were queued, or 409 if a job is already running.</returns>
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

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        if (!SeriesSyncService.IsSeriesOrSeason(item))
        {
            return BadRequest("That item isn't a series or a season");
        }

        var episodes = _seriesSyncService.GetEpisodes(item)
            .Where(_libraryService.IsEligible)
            .ToList();

        if (episodes.Count == 0)
        {
            return Conflict("No episodes here are eligible for syncing - check the library is turned on and nothing is skipped");
        }

        var referenceKey = string.IsNullOrWhiteSpace(request.ReferenceKey) ? null : request.ReferenceKey.Trim();

        if (!_queueManager.EnqueueItems(episodes, item.Name ?? "Series", "episode", referenceKey))
        {
            return Conflict("A sync job is already running");
        }

        return Accepted(new { Queued = episodes.Count });
    }

    /// <summary>
    /// Lists the series in one library, for the dashboard's season picker.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>One entry per series.</returns>
    [HttpGet("Lapse/Libraries/{libraryId}/Series")]
    public ActionResult<List<FolderEntry>> GetSeriesInLibrary([FromRoute] Guid libraryId)
    {
        return _seriesSyncService.GetSeries(libraryId)
            .Select(series => new FolderEntry { ItemId = series.Id, Name = series.Name ?? "Unknown" })
            .ToList();
    }

    /// <summary>
    /// Lists the seasons of one series, for the dashboard's season picker.
    /// </summary>
    /// <param name="itemId">The series.</param>
    /// <returns>One entry per season.</returns>
    [HttpGet("Lapse/Series/{itemId}/Seasons")]
    public ActionResult<List<FolderEntry>> GetSeasons([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return _seriesSyncService.GetSeasons(item)
            .Select(season => new FolderEntry { ItemId = season.Id, Name = season.Name ?? "Unknown" })
            .ToList();
    }

    /// <summary>
    /// Starts a sync of every series in one library, one episode at a time.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>How many episodes were queued, or 409 if a job is already running.</returns>
    [HttpPost("Lapse/Libraries/{libraryId}/SyncAllSeries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SyncAllSeries([FromRoute] Guid libraryId)
    {
        var episodes = _libraryService.GetItems(libraryId)
            .Where(_libraryService.IsEligible)
            .ToList();

        if (episodes.Count == 0)
        {
            return Conflict("Nothing in that library is eligible for syncing");
        }

        var name = _libraryService.GetLibraries().FirstOrDefault(l => l.ItemId == libraryId)?.Name ?? "Library";

        if (!_queueManager.EnqueueItems(episodes, name, "episode"))
        {
            return Conflict("A sync job is already running");
        }

        return Accepted(new { Queued = episodes.Count });
    }

    /// <summary>
    /// Marks (or unmarks) an item or folder as skipped.
    /// </summary>
    /// <param name="request">The item and whether it should be skipped.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Skip")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Skip([FromBody] SkipRequest request)
    {
        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var config = Plugin.Instance!.Configuration;

        if (request.Skip)
        {
            if (!config.SkippedItemIds.Contains(request.ItemId))
            {
                config.SkippedItemIds.Add(request.ItemId);
            }
        }
        else
        {
            config.SkippedItemIds.Remove(request.ItemId);
        }

        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    // -------------------------------------------------------------------- libraries

    /// <summary>
    /// Gets every library, with its LAPSE settings.
    /// </summary>
    /// <returns>List of libraries.</returns>
    [HttpGet("Lapse/Libraries")]
    public ActionResult<List<LibraryEntry>> GetLibraries()
    {
        return _libraryService.GetLibraries();
    }

    /// <summary>
    /// Saves the per-library enable and schedule settings.
    /// </summary>
    /// <param name="request">The libraries as the dashboard has them.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveLibraries([FromBody] LibrarySettingsRequest request)
    {
        var config = Plugin.Instance!.Configuration;
        var names = _libraryService.GetLibraries().ToDictionary(l => l.ItemId, l => l.Name);

        if (request?.Libraries is null || request.Libraries.Count == 0)
        {
            return BadRequest("No libraries were sent, so there was nothing to save");
        }

        foreach (var entry in request.Libraries)
        {
            var library = config.GetLibraryConfig(entry.ItemId);
            var wasScheduled = library.ScheduleEnabled;
            var oldFrequency = library.ScheduleFrequency;
            var oldDay = library.ScheduleDay;
            var oldTime = library.ScheduleTime;

            library.Enabled = entry.Enabled;
            library.AutoSyncEnabled = entry.AutoSyncEnabled;

            // New items and a schedule are one choice, not two: a library either picks
            // things up as they arrive or sweeps the whole thing on a timer. The form
            // only ever lets one be ticked, and this is what makes that true of the
            // stored config as well rather than only of the page.
            library.ScheduleEnabled = entry.ScheduleEnabled && !entry.AutoSyncEnabled;
            library.ScheduleFrequency = Enum.TryParse<ScheduleFrequency>(entry.ScheduleFrequency, out var frequency)
                ? frequency
                : ScheduleFrequency.Daily;
            library.ScheduleDay = Enum.TryParse<DayOfWeek>(entry.ScheduleDay, out var day) ? day : null;
            library.ScheduleTime = string.IsNullOrWhiteSpace(entry.ScheduleTime) ? "03:00" : entry.ScheduleTime;

            if (names.TryGetValue(entry.ItemId, out var name))
            {
                library.Name = name;
            }

            // A schedule that was just turned on, or retimed, should be free to fire at
            // its next slot rather than being held back by when the old one last ran.
            var changed = library.ScheduleFrequency != oldFrequency
                || library.ScheduleDay != oldDay
                || !string.Equals(library.ScheduleTime, oldTime, StringComparison.Ordinal);

            if ((!wasScheduled && library.ScheduleEnabled) || changed)
            {
                library.LastScheduledRunUtc = null;
            }
        }

        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    /// <summary>
    /// Gets the top level library folders, for the bulk sync folder dropdown.
    /// </summary>
    /// <returns>List of folders.</returns>
    [HttpGet("Lapse/Folders")]
    public ActionResult<List<FolderEntry>> GetFolders()
    {
        var config = Plugin.Instance!.Configuration;

        return _libraryManager.GetVirtualFolders()
            .Where(folder => Guid.TryParse(folder.ItemId, out _))
            .Select(folder =>
            {
                var id = Guid.Parse(folder.ItemId);
                return new FolderEntry
                {
                    ItemId = id,
                    Name = folder.Name,
                    Skipped = config.SkippedItemIds.Contains(id)
                };
            })
            .ToList();
    }

    // ---------------------------------------------------------------------- engines

    /// <summary>
    /// Gets every engine the plugin knows about, whether it's usable, what it can do, and
    /// where it stands against its published releases.
    ///
    /// The release check runs here rather than only behind the "Check for updates" button,
    /// so opening the dashboard is enough to find out something newer is out. It isn't a
    /// network call per load: GitHubReleaseClient caches its answer for half an hour and
    /// this asks without forcing, so a page refresh costs nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per engine.</returns>
    [HttpGet("Lapse/Engines")]
    public async Task<ActionResult<List<EngineInfo>>> GetEngines(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var defaultId = _registry.GetDefault().Descriptor.Id;
        var result = new List<EngineInfo>();

        foreach (var engine in _registry.All)
        {
            var descriptor = engine.Descriptor;
            var settings = config.GetEngineSettings(descriptor.Id);
            var path = _runner.ResolvePath(engine);
            var installed = System.IO.File.Exists(path);
            var downloadable = EngineRunner.HasDownload(engine);

            var runtime = installed
                ? await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false)
                : EngineRuntimeInfo.Unknown;

            var status = await _updater.CheckAsync(engine, force: false, cancellationToken).ConfigureAwait(false);
            var values = EngineRunner.ResolveParameters(engine);

            var info = new EngineInfo
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Description = descriptor.Description,
                ProjectUrl = descriptor.ProjectUrl,
                BuildGuideUrl = descriptor.BuildGuideUrl,
                Installed = installed,
                Path = path,
                IsDefault = string.Equals(descriptor.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                Tier = descriptor.Tier.ToString(),
                WhyUrl = descriptor.WhyUrl,
                WhyLabel = descriptor.WhyLabel,
                AdvancedNote = descriptor.AdvancedNote,
                DeploymentNote = descriptor.DeploymentNote,
                SubtitleExtensions = installed
                    ? runtime.GetSubtitleExtensions(descriptor.Id)
                    : descriptor.Capabilities.SubtitleExtensions,
                DownloadSupported = downloadable,
                NoDownloadReason = downloadable ? null : EngineInstaller.DescribeMissingBuild(engine),
                RunCheckError = installed ? await _runner.CheckRunnableAsync(engine, cancellationToken).ConfigureAwait(false) : null,
                SupportsStandard = descriptor.Capabilities.SupportsStandard,
                SupportsAuto = descriptor.Capabilities.SupportsAuto,
                SupportsOls = descriptor.Capabilities.SupportsOls,
                SupportsSplit = descriptor.Capabilities.SupportsSplit,
                SupportsPenalty = descriptor.Capabilities.SupportsPenalty,
                Penalty = EngineRunner.ResolvePenalty(engine, null),
                DefaultPenalty = descriptor.Capabilities.DefaultPenalty,
                MinPenalty = descriptor.Capabilities.MinPenalty,
                MaxPenalty = descriptor.Capabilities.MaxPenalty,
                DefaultMode = EngineRunner.ResolveDefaultMode(engine).ToString(),
                PathOverride = settings.PathOverride,
                InstalledVersion = status.InstalledVersion,
                LatestVersion = status.LatestVersion,
                VersionUnknown = status.VersionUnknown,
                UpdateAvailable = status.UpdateAvailable,
                AutoUpdate = config.AutoUpdateEngines,
                ReportedVersion = runtime.Version,
                DiscoveredFlags = runtime.Probed ? runtime.Flags : null,
                CapabilitySource = runtime.Source,
                SupportsOutputFlag = runtime.SupportsOutputFlag,
                SupportsNoBackupFlag = runtime.SupportsNoBackupFlag
            };

            foreach (var mode in descriptor.Modes)
            {
                info.Modes.Add(new EngineModeInfo
                {
                    Value = mode.Mode.ToString(),
                    Label = mode.Label,
                    Description = mode.Description
                });
            }

            foreach (var parameter in descriptor.Parameters)
            {
                var entry = new EngineParameterInfo
                {
                    Key = parameter.Key,
                    Label = parameter.Label,
                    Description = parameter.Description,
                    Flag = parameter.Flag,
                    Kind = parameter.Kind.ToString(),
                    DefaultValue = parameter.DefaultValue,
                    Value = values.GetString(parameter.Key),
                    Minimum = parameter.Minimum,
                    Maximum = parameter.Maximum,
                    Step = parameter.Step,
                    BlankMeansUnset = parameter.BlankMeansUnset
                };

                foreach (var option in parameter.Options)
                {
                    entry.Options.Add(new EngineModeInfo { Value = option.Value, Label = option.Label });
                }

                info.Parameters.Add(entry);
            }

            result.Add(info);
        }

        return result;
    }

    /// <summary>
    /// Asks GitHub what the newest release of each engine is. Separate from GET Engines
    /// so that loading the dashboard doesn't have to wait on the network.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One status per engine.</returns>
    [HttpGet("Lapse/Engines/Updates")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<List<EngineUpdateStatus>>> CheckEngineUpdates(CancellationToken cancellationToken)
    {
        var result = new List<EngineUpdateStatus>();

        foreach (var engine in _registry.All)
        {
            result.Add(await _updater.CheckAsync(engine, force: true, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Downloads and installs one engine.
    /// </summary>
    /// <param name="engineId">Which engine.</param>
    /// <returns>Ok, or a 400 explaining why it couldn't be installed.</returns>
    [HttpPost("Lapse/Engines/{engineId}/Install")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> InstallEngine([FromRoute] string engineId)
    {
        var engine = _registry.Find(engineId);
        if (engine is null)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        try
        {
            var version = await _installer.InstallAsync(engine).ConfigureAwait(false);
            return Ok(new { Version = version });
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Removes an engine's installed copy, freeing the disk it took. Only ever deletes the
    /// plugin's own engine folder: a binary behind a path override was not put there by
    /// the plugin and is not the plugin's to delete, so that is refused rather than
    /// quietly removing someone's hand built build.
    /// </summary>
    /// <param name="engineId">Which engine.</param>
    /// <returns>Ok, or a 400 explaining why it wasn't removed.</returns>
    [HttpPost("Lapse/Engines/{engineId}/Uninstall")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult UninstallEngine([FromRoute] string engineId)
    {
        var engine = _registry.Find(engineId);
        if (engine is null)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        var settings = Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id);
        if (!string.IsNullOrWhiteSpace(settings.PathOverride))
        {
            return BadRequest(
                "This engine is pointed at your own binary, so there's nothing here to remove. "
                + "Clear the binary path override first if you want to stop using it.");
        }

        var installedPath = _runner.GetInstalledPath(engine);
        var folder = Path.GetDirectoryName(installedPath);

        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            else if (System.IO.File.Exists(installedPath))
            {
                System.IO.File.Delete(installedPath);
            }
            else
            {
                return BadRequest("That engine isn't installed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BadRequest("Could not remove it: " + ex.Message);
        }

        // Nothing is on disk any more, so the recorded version would be a lie, and the
        // probe's cached answer is about a file that has gone.
        settings.InstalledVersion = null;
        Plugin.Instance!.SaveConfiguration();
        _probe.Invalidate();

        return Ok();
    }

    /// <summary>
    /// Updates one engine to the newest release.
    /// </summary>
    /// <param name="engineId">Which engine.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A line saying what happened.</returns>
    [HttpPost("Lapse/Engines/{engineId}/Update")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> UpdateEngine([FromRoute] string engineId, CancellationToken cancellationToken)
    {
        var engine = _registry.Find(engineId);
        if (engine is null)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        try
        {
            var outcome = await _updater.UpdateAsync(engine, force: false, cancellationToken).ConfigureAwait(false);
            return Ok(new { Outcome = outcome });
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Turns automatic engine updates on or off for the whole server. There used to be
    /// one of these per engine, which meant four switches to answer one question.
    /// Engines that aren't installed are ignored by the update task either way.
    /// </summary>
    /// <param name="enabled">Whether the daily task keeps installed engines up to date.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Engines/AutoUpdate")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetEngineAutoUpdate([FromQuery] bool enabled)
    {
        Plugin.Instance!.Configuration.AutoUpdateEngines = enabled;
        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    /// <summary>
    /// Installs every engine that has a build for this machine. Engines that don't are
    /// skipped rather than failing the whole thing.
    /// </summary>
    /// <returns>A short summary of what happened per engine.</returns>
    [HttpPost("Lapse/Engines/InstallAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> InstallAllEngines()
    {
        var results = new Dictionary<string, string>();

        foreach (var engine in _registry.All)
        {
            if (!EngineRunner.HasDownload(engine))
            {
                results[engine.Descriptor.Id] = "skipped, no build for this machine";
                continue;
            }

            try
            {
                var version = await _installer.InstallAsync(engine).ConfigureAwait(false);
                results[engine.Descriptor.Id] = version is null ? "installed" : "installed " + version;
            }
            catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
            {
                results[engine.Descriptor.Id] = "failed: " + ex.Message;
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Sets which engine syncs use when they don't ask for a specific one.
    /// </summary>
    /// <param name="engineId">Which engine.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Engines/{engineId}/Default")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetDefaultEngine([FromRoute] string engineId)
    {
        var engine = _registry.Find(engineId);
        if (engine is null)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        Plugin.Instance!.Configuration.DefaultEngineId = engine.Descriptor.Id;
        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    /// <summary>
    /// Gets the server's users, so the settings page can offer a list to pick from when
    /// subtitle access is handed to named people.
    /// </summary>
    /// <returns>One entry per user.</returns>
    [HttpGet("Lapse/Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<object>> GetUsers()
    {
        return _userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => (object)new
            {
                Id = u.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = u.Username,
                IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                CanManageSubtitles = u.HasPermission(PermissionKind.EnableSubtitleManagement)
            })
            .ToList();
    }

    // --------------------------------------------------------------------- settings

    /// <summary>
    /// Gets the output and translation settings.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet("Lapse/Settings")]
    public ActionResult<PluginSettings> GetSettings()
    {
        var config = Plugin.Instance!.Configuration;

        var settings = new PluginSettings
        {
            OutputMode = config.OutputMode,
            SidecarSuffix = config.SidecarSuffix,
            LowConfidenceAction = config.LowConfidenceAction,
            ConfidenceSigma = config.ConfidenceSigma,
            SubToSubPlacement = config.SubToSubPlacement,
            SubToSubCustomFolder = config.SubToSubCustomFolder,
            // srt is the default this ships with, so a config written before this setting
            // existed - or one holding a format that is no longer offered - reads back as
            // srt rather than leaving the dashboard to pick something for it.
            ConversionFormat = SubtitleFormats.TryNormalizeOutputFormat(config.ConversionFormat, out var storedFormat)
                ? storedFormat
                : "srt",
            ConversionReplaceOriginal = config.ConversionReplaceOriginal,
            ConversionSyncAfter = config.ConversionSyncAfter,
            ConvertBeforeSync = config.ConvertBeforeSync,
            AutomationAction = config.AutomationAction,
            AutoTranslateEnabled = config.AutoTranslateEnabled,
            AutoTranslateLanguage = config.AutoTranslateLanguage,
            AutoTranslateSkipExisting = config.AutoTranslateSkipExisting,
            SubtitleAccess = config.SubtitleAccess,
            SubtitleAccessUserIds = new List<string>(config.SubtitleAccessUserIds),
            OpenSubtitlesEnabled = config.OpenSubtitlesEnabled,
            OpenSubtitlesApiKey = config.OpenSubtitlesApiKey,
            OpenSubtitlesUsername = config.OpenSubtitlesUsername,
            OpenSubtitlesPassword = config.OpenSubtitlesPassword,
            OpenSubtitlesLanguage = config.OpenSubtitlesLanguage,
            ArrWebhookEnabled = config.ArrWebhookEnabled,
            ArrWebhookToken = config.ArrWebhookToken,
            AutoUpdateEngines = config.AutoUpdateEngines,
            GoogleTranslateApiKey = config.GoogleTranslateApiKey,
            DeepLApiKey = config.DeepLApiKey,
            LingarrBaseUrl = config.LingarrBaseUrl,
            LingarrApiKey = config.LingarrApiKey,
            LibreTranslateBaseUrl = config.LibreTranslateBaseUrl,
            LibreTranslateApiKey = config.LibreTranslateApiKey,
            DefaultTranslationProvider = config.DefaultTranslationProvider,
            TranslationConfidenceThreshold = config.TranslationConfidenceThreshold,
            TranslationIncludeMetadataHeader = config.TranslationIncludeMetadataHeader,
            TranslationKeepLowConfidenceOriginal = config.TranslationKeepLowConfidenceOriginal,
            SubtitleAppearance = config.SubtitleAppearance
        };

        foreach (var engine in _registry.All)
        {
            var engineSettings = config.GetEngineSettings(engine.Descriptor.Id);
            var entry = new EngineSettingsEntry
            {
                EngineId = engine.Descriptor.Id,
                PathOverride = engineSettings.PathOverride,
                Penalty = engineSettings.Penalty,
                DefaultMode = EngineRunner.ResolveDefaultMode(engine).ToString()
            };

            var values = EngineRunner.ResolveParameters(engine);
            foreach (var parameter in engine.Descriptor.Parameters)
            {
                entry.Parameters.Add(new EngineParameterEntry
                {
                    Key = parameter.Key,
                    Value = values.GetString(parameter.Key)
                });
            }

            settings.Engines.Add(entry);
        }

        return settings;
    }

    /// <summary>
    /// Gets just the subtitle appearance settings. Separate from the rest because the
    /// injected script needs these on every page load for any signed in user, not only
    /// for an admin sitting on the dashboard.
    /// </summary>
    /// <returns>The appearance settings.</returns>
    [HttpGet("Lapse/Appearance")]
    public ActionResult<SubtitleAppearance> GetAppearance()
    {
        return Plugin.Instance!.Configuration.SubtitleAppearance;
    }

    /// <summary>
    /// Says whether the plugin is actually reaching the web client, which is what decides
    /// whether the sync entries show up in an item's context menu at all.
    /// </summary>
    /// <returns>The injection status.</returns>
    [HttpGet("Lapse/Diagnostics")]
    public ActionResult<object> GetDiagnostics()
    {
        var method = WebClientInjection.Evaluate();

        return new
        {
            InjectionMethod = method.ToString(),
            Working = method != InjectionMethod.None,
            WebClientInjection.Problem,
            WebClientInjection.WebPath,
            Platform = EngineInstaller.DetectedOsArch,
            EngineInstaller.TargetArchitecture,
            EngineInstaller.ProcessArchitecture,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            InContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            ArrWebhookService.LastEvent,
            ArrWebhookLastEventUtc = ArrWebhookService.LastEventUtc
        };
    }

    // ------------------------------------------------------------------- ignore list

    /// <summary>
    /// Gets the ignore list.
    /// </summary>
    /// <returns>The rules, newest first.</returns>
    [HttpGet("Lapse/Ignore")]
    public ActionResult<List<IgnoreRule>> GetIgnoreRules()
    {
        return Plugin.Instance!.Configuration.IgnoreRules
            .OrderByDescending(r => r.AddedUtc)
            .ToList();
    }

    /// <summary>
    /// Adds something to the ignore list, by item or by path.
    /// </summary>
    /// <param name="rule">What to ignore.</param>
    /// <returns>Ok, or 400 when the rule names neither an item nor a path.</returns>
    [HttpPost("Lapse/Ignore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult AddIgnoreRule([FromBody] IgnoreRule rule)
    {
        if (rule is null || (!rule.ItemId.HasValue && string.IsNullOrWhiteSpace(rule.Path)))
        {
            return BadRequest("An ignore rule needs either an item or a path");
        }

        var config = Plugin.Instance!.Configuration;

        if (rule.ItemId.HasValue)
        {
            var item = _libraryManager.GetItemById(rule.ItemId.Value);
            if (item is null)
            {
                return NotFound("Item not found");
            }

            if (config.IgnoreRules.Any(r => r.ItemId == rule.ItemId))
            {
                return Ok();
            }

            config.IgnoreRules.Add(new IgnoreRule
            {
                ItemId = item.Id,
                DisplayName = SyncQueueManager.DescribeItem(item),
                Kind = item.GetBaseItemKind().ToString(),
                Path = null
            });
        }
        else
        {
            var path = rule.Path!.Trim();

            if (config.IgnoreRules.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return Ok();
            }

            config.IgnoreRules.Add(new IgnoreRule
            {
                Path = path,
                DisplayName = path,
                Kind = "Path"
            });
        }

        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    /// <summary>
    /// Takes something off the ignore list.
    /// </summary>
    /// <param name="itemId">The item to un-ignore, if it was an item rule.</param>
    /// <param name="path">The path to un-ignore, if it was a path rule.</param>
    /// <returns>How many rules were taken off.</returns>
    [HttpDelete("Lapse/Ignore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IgnoreRemovalResult> RemoveIgnoreRule([FromQuery] Guid? itemId, [FromQuery] string? path)
    {
        if (!itemId.HasValue && string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Say which item or path to take off the list");
        }

        var config = Plugin.Instance!.Configuration;

        var removed = config.IgnoreRules.RemoveAll(r =>
            (itemId.HasValue && r.ItemId == itemId)
            || (!string.IsNullOrWhiteSpace(path) && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)));

        if (removed > 0)
        {
            Plugin.Instance!.SaveConfiguration();
        }

        // An item can be ignored because a rule on its series or its folder covers it, and
        // there is nothing on its own id to take off. Saying so beats a button that looks
        // like it did nothing.
        return new IgnoreRemovalResult { Removed = removed };
    }

    // --------------------------------------------------------- radarr / sonarr webhook

    /// <summary>
    /// Takes a Radarr or Sonarr webhook and syncs whatever was imported.
    ///
    /// Anonymous on purpose: neither app can send a Jellyfin API key, so the shared secret
    /// in the query string is what stands between this and anyone who can reach the
    /// server. It's compared in full, the endpoint does nothing at all until it's turned
    /// on in the settings, and the only thing a valid call can do is queue a sync for a
    /// file that is already in the library.
    /// </summary>
    /// <param name="token">The shared secret from the settings.</param>
    /// <param name="payload">The webhook body.</param>
    /// <returns>Ok when the notification was understood.</returns>
    [HttpPost("Lapse/Webhook/Arr")]
    [AllowAnonymous]
    public ActionResult ArrWebhook([FromQuery] string? token, [FromBody] JsonElement payload)
    {
        var config = Plugin.Instance!.Configuration;

        if (!config.ArrWebhookEnabled || string.IsNullOrWhiteSpace(config.ArrWebhookToken))
        {
            return NotFound();
        }

        if (!string.Equals(token, config.ArrWebhookToken, StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        var eventType = ArrWebhookService.ReadEventType(payload);
        ArrWebhookService.LastEvent = eventType ?? "(no event type)";
        ArrWebhookService.LastEventUtc = DateTime.UtcNow;

        // Radarr and Sonarr both fire a Test when you press the button in their UI. Say
        // yes to it so the connection tests green, and do nothing else.
        if (eventType is "test" or null)
        {
            return Ok(new { Message = "LAPSE is listening." });
        }

        // Grabs, renames, health checks and deletes aren't imports. Only the events that
        // put a new file on disk are worth a sync.
        if (eventType is not ("download" or "moviefileimported" or "episodefileimported" or "movieadded"))
        {
            return Ok(new { Message = "Nothing to do for " + eventType });
        }

        var paths = ArrWebhookService.ReadPaths(payload);
        if (paths.Count == 0)
        {
            return Ok(new { Message = "No video file path in that notification" });
        }

        // Answer now and do the waiting in the background: both apps treat a webhook that
        // takes its time as a failed one, and the item won't be in the library yet anyway.
        _ = Task.Run(() => _arrWebhookService.SyncWhenScannedAsync(paths, CancellationToken.None));

        return Ok(new { Queued = paths.Count });
    }

    /// <summary>
    /// Makes a new shared secret for the webhook URL, replacing whatever was there.
    /// </summary>
    /// <returns>The new token.</returns>
    [HttpPost("Lapse/Webhook/Arr/Token")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult NewArrWebhookToken()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        Plugin.Instance!.Configuration.ArrWebhookToken = token;
        Plugin.Instance!.SaveConfiguration();

        return Ok(new { Token = token });
    }

    /// <summary>
    /// Saves the output and translation settings, plus the per-engine tunables.
    /// </summary>
    /// <param name="settings">The settings form.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveSettings([FromBody] PluginSettings settings)
    {
        var config = Plugin.Instance!.Configuration;

        config.OutputMode = settings.OutputMode;
        config.SidecarSuffix = string.IsNullOrWhiteSpace(settings.SidecarSuffix) ? ".shifted" : settings.SidecarSuffix.Trim();
        config.LowConfidenceAction = settings.LowConfidenceAction;

        // The engine rejects anything at or below zero, and a runaway value would just mean
        // nothing is ever confident enough to write.
        config.ConfidenceSigma = Math.Clamp(settings.ConfidenceSigma, 0.5, 30);

        config.SubToSubPlacement = settings.SubToSubPlacement;
        config.SubToSubCustomFolder = Blank(settings.SubToSubCustomFolder);
        config.ConversionFormat = SubtitleFormats.TryNormalizeOutputFormat(settings.ConversionFormat, out var conversionFormat)
            ? conversionFormat
            : "srt";
        config.ConversionReplaceOriginal = settings.ConversionReplaceOriginal;
        config.ConversionSyncAfter = settings.ConversionSyncAfter;
        config.ConvertBeforeSync = settings.ConvertBeforeSync;
        config.AutomationAction = settings.AutomationAction;
        config.AutoTranslateEnabled = settings.AutoTranslateEnabled;
        config.AutoTranslateLanguage = Blank(settings.AutoTranslateLanguage);
        config.AutoTranslateSkipExisting = settings.AutoTranslateSkipExisting;

        // Translating into nothing would run the whole library through a provider and
        // write files named after an empty language tag, so it stays off until asked for.
        if (string.IsNullOrEmpty(config.AutoTranslateLanguage))
        {
            config.AutoTranslateEnabled = false;
        }

        config.SubtitleAccess = settings.SubtitleAccess;

        // Keep only ids that are still real users, so deleting a user doesn't leave a
        // stale grant sitting in the config waiting for a recycled id.
        config.SubtitleAccessUserIds = settings.SubtitleAccessUserIds
            .Where(id => Guid.TryParse(id, out var parsed) && _userManager.GetUserById(parsed) is not null)
            .Select(id => Guid.Parse(id).ToString("N", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.OpenSubtitlesEnabled = settings.OpenSubtitlesEnabled;
        config.OpenSubtitlesApiKey = Blank(settings.OpenSubtitlesApiKey);
        config.OpenSubtitlesUsername = Blank(settings.OpenSubtitlesUsername);
        config.OpenSubtitlesPassword = Blank(settings.OpenSubtitlesPassword);
        config.OpenSubtitlesLanguage = Blank(settings.OpenSubtitlesLanguage) ?? "en";
        config.ArrWebhookEnabled = settings.ArrWebhookEnabled;
        config.AutoUpdateEngines = settings.AutoUpdateEngines;
        config.GoogleTranslateApiKey = Blank(settings.GoogleTranslateApiKey);
        config.DeepLApiKey = Blank(settings.DeepLApiKey);
        config.LingarrBaseUrl = Blank(settings.LingarrBaseUrl);
        config.LingarrApiKey = Blank(settings.LingarrApiKey);
        config.LibreTranslateBaseUrl = Blank(settings.LibreTranslateBaseUrl);
        config.LibreTranslateApiKey = Blank(settings.LibreTranslateApiKey);
        config.DefaultTranslationProvider = settings.DefaultTranslationProvider;
        config.TranslationConfidenceThreshold = Math.Clamp(settings.TranslationConfidenceThreshold, 0, 100);
        config.TranslationIncludeMetadataHeader = settings.TranslationIncludeMetadataHeader;
        config.TranslationKeepLowConfidenceOriginal = settings.TranslationKeepLowConfidenceOriginal;

        if (settings.SubtitleAppearance is not null)
        {
            var appearance = settings.SubtitleAppearance;
            config.SubtitleAppearance = new SubtitleAppearance
            {
                Enabled = appearance.Enabled,
                FontSizePx = Math.Clamp(appearance.FontSizePx, 8, 200),
                TextColor = NormalizeColor(appearance.TextColor, SubtitleAppearance.DefaultTextColor),
                BackgroundColor = NormalizeColor(appearance.BackgroundColor, SubtitleAppearance.DefaultBackgroundColor),
                BackgroundEnabled = appearance.BackgroundEnabled
            };
        }

        foreach (var entry in settings.Engines)
        {
            var engine = _registry.Find(entry.EngineId);
            if (engine is null)
            {
                continue;
            }

            var engineSettings = config.GetEngineSettings(entry.EngineId);
            engineSettings.PathOverride = Blank(entry.PathOverride);
            engineSettings.Penalty = entry.Penalty;

            if (Enum.TryParse<SyncMode>(entry.DefaultMode, ignoreCase: true, out var mode)
                && engine.Descriptor.Modes.Exists(m => m.Mode == mode))
            {
                engineSettings.DefaultMode = mode.ToString();
            }

            // Only take keys this engine actually has, so a stale form from an older
            // version can't leave dead settings sitting in the config forever.
            var known = new HashSet<string>(
                engine.Descriptor.Parameters.Select(p => p.Key),
                StringComparer.OrdinalIgnoreCase);

            engineSettings.SetParameters(entry.Parameters
                .Where(p => known.Contains(p.Key))
                .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value?.Trim())));
        }

        Plugin.Instance!.SaveConfiguration();
        return Ok();
    }

    // ------------------------------------------------------------------ translation

    /// <summary>
    /// Translates one of an item's subtitles into another language, writing a new file
    /// next to it. The source subtitle is never modified.
    /// </summary>
    /// <param name="request">The job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Lapse/Translate")]
    public async Task<ActionResult<TranslationResult>> Translate(
        [FromBody] TranslationRequest request,
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

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        // same rule as syncing: only ever read a subtitle the library lists for this item,
        // and use the library's copy of the path rather than the one in the request
        var match = _subtitleLocator.GetExternalSubtitles(item)
            .Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));

        if (match is null)
        {
            return BadRequest("That subtitle doesn't belong to this item");
        }

        var (translatePath, translateError) = await ResolveToFileAsync(item, match, cancellationToken).ConfigureAwait(false);
        if (translatePath is null)
        {
            return BadRequest(translateError ?? "Could not get that subtitle out of the video file.");
        }

        return await _translationService.TranslateAsync(translatePath, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the translation providers, in the order they should be offered: the one that
    /// needs no setting up first, then the self hosted ones, then the ones wanting a
    /// cloud API key. Anything not configured comes back with the reason, so the
    /// dashboard can grey it out and the per-job dropdown can leave it out entirely.
    /// </summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("Lapse/Translate/Providers")]
    public ActionResult<List<object>> GetTranslationProviders()
    {
        var defaultProvider = Plugin.Instance!.Configuration.DefaultTranslationProvider;

        return _translationService.Providers
            .Select(p =>
            {
                var problem = p.GetConfigurationProblem();
                return (object)new
                {
                    Id = p.Id.ToString(),
                    p.DisplayName,
                    p.Tier,
                    p.Summary,
                    Problem = problem,
                    Configured = problem is null,
                    IsDefault = p.Id == defaultProvider
                };
            })
            .ToList();
    }

    // --------------------------------------------------------------- manual tinkering

    /// <summary>
    /// Nudges a subtitle file's timings by hand, for when a sync gets close but is still
    /// slightly off. Doesn't touch the engine, it just rewrites the timestamps. Where the
    /// result lands follows the configured output mode, same as a sync.
    /// </summary>
    /// <param name="request">Which subtitle and how far to move it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the shift did.</returns>
    [HttpPost("Lapse/Shift")]
    public async Task<ActionResult<ShiftResult>> Shift(
        [FromBody] ShiftRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            return BadRequest("A subtitle path is required");
        }

        // Only ever write to a subtitle the library actually knows about for this item.
        // The path we hand to the shifter is the library's own copy, not the one from the
        // request body, so there's no way to talk this into editing some other file.
        var match = FindSubtitle(request.ItemId, request.SubtitlePath);
        if (match is null)
        {
            return BadRequest("That subtitle doesn't belong to this item, or the item doesn't exist");
        }

        var (shiftPath, shiftError) = await ResolveToFileAsync(
            _libraryManager.GetItemById(request.ItemId)!,
            match,
            cancellationToken).ConfigureAwait(false);

        if (shiftPath is null)
        {
            return BadRequest(shiftError ?? "Could not get that subtitle out of the video file.");
        }

        try
        {
            return await _subtitleShifter
                .ShiftAsync(shiftPath, request.ResolveOffsetSeconds(), request.OutputMode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Writes one of an item's subtitles out in a different format, without syncing it.
    /// The source is left alone unless the request asks for it to be replaced.
    ///
    /// This is also the way in for the formats no engine reads. A MicroDVD .sub can't be
    /// synced or shifted as it stands, but converting it to srt first makes it an
    /// ordinary subtitle that everything else here works on.
    /// </summary>
    /// <param name="request">Which subtitle, and what to write it as.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was written.</returns>
    [HttpPost("Lapse/Convert")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The source is a subtitle the library lists for this item and the destination is that path with a different extension.")]
    public async Task<ActionResult<ConvertResult>> ConvertSubtitle(
        [FromBody] ConvertRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            return BadRequest("A subtitle path is required");
        }

        var config = Plugin.Instance!.Configuration;

        // An empty format means "whatever the Conversion settings say", so the menu entry
        // doesn't have to know what that is.
        var requestedFormat = string.IsNullOrWhiteSpace(request.TargetFormat) ? config.ConversionFormat : request.TargetFormat;

        if (!SubtitleFormats.TryNormalizeOutputFormat(requestedFormat, out var target))
        {
            return BadRequest("The target format has to be srt, vtt, ass or ssa");
        }

        var replaceOriginal = request.ReplaceOriginal ?? config.ConversionReplaceOriginal;

        // Same rule as everywhere else: only ever read a subtitle the library lists for
        // this item, and work from its copy of the path rather than the request's.
        var match = FindSubtitle(request.ItemId, request.SubtitlePath);
        if (match is null)
        {
            return BadRequest("That subtitle doesn't belong to this item, or the item doesn't exist");
        }

        var (sourcePath, sourceError) = await ResolveToFileAsync(
            _libraryManager.GetItemById(request.ItemId)!,
            match,
            cancellationToken).ConfigureAwait(false);

        if (sourcePath is null)
        {
            return BadRequest(sourceError ?? "Could not get that subtitle out of the video file.");
        }

        var sourceFormat = SubtitleFormats.GetName(sourcePath);
        if (string.Equals(sourceFormat, target, StringComparison.Ordinal))
        {
            return BadRequest($"That subtitle is already {target}.");
        }

        var destination = Path.ChangeExtension(sourcePath, "." + target);

        // Converting shouldn't be able to write over a subtitle that's already there -
        // most obviously the file this one was converted from last time.
        var attempt = 1;
        while (System.IO.File.Exists(destination))
        {
            destination = Path.ChangeExtension(sourcePath, null)
                + "." + attempt.ToString(CultureInfo.InvariantCulture) + "." + target;
            attempt++;
        }

        try
        {
            var cues = await _converter.ConvertAsync(sourcePath, destination, cancellationToken).ConfigureAwait(false);

            var removed = false;

            // An embedded track was extracted to get here, so there is no original to
            // replace - the file at sourcePath is one this request just made, and the
            // track is still in the video either way.
            if (replaceOriginal && !match.IsEmbedded)
            {
                try
                {
                    System.IO.File.Delete(sourcePath);
                    removed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The conversion itself worked, so reporting a failure over a file
                    // that is merely still there would be the wrong answer. RemovedOriginal
                    // stays false, which is what the caller needs to know.
                    _ = ex;
                }
            }

            var result = new ConvertResult
            {
                OutputPath = destination,
                Format = target,
                SourceFormat = sourceFormat,
                Cues = cues,
                RemovedOriginal = removed
            };

            // Converting is usually a step on the way to syncing rather than the point of
            // the exercise, so unless told otherwise it carries straight on.
            if (request.SyncAfter ?? config.ConversionSyncAfter)
            {
                var item = _libraryManager.GetItemById(request.ItemId);

                if (item is not null && !string.IsNullOrEmpty(item.Path))
                {
                    var engine = _registry.Resolve(request.EngineId);

                    result.Sync = await _runner
                        .RunAsync(
                            engine,
                            item.Path,
                            destination,
                            EngineRunner.ResolveDefaultMode(engine),
                            EngineRunner.ResolvePenalty(engine, null),
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    result.SyncedAfter = true;

                    SyncQueueManager.SaveRecord(
                        request.ItemId,
                        ResolveRecordStatus(result.Sync),
                        result.Sync.Success && result.Sync.Skipped ? SyncQueueManager.DescribeSkip(result.Sync) : result.Sync.Error,
                        result.Sync,
                        result.Sync.Success && !result.Sync.Skipped ? new[] { destination } : null);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Runs several steps over one subtitle in a single request: change its format, sync
    /// it, translate it, or any combination. The order is fixed, because it's the only one
    /// that works - the format has to change before an engine can read the file, and the
    /// timings have to be right before a translation copies them.
    ///
    /// Doing this server side rather than as three calls from the dialog matters: each
    /// step works on the file the one before it produced, and that file doesn't exist yet
    /// when the browser would have to name it.
    /// </summary>
    /// <param name="request">Which subtitle, and which steps to run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What each step did.</returns>
    [HttpPost("Lapse/Pipeline")]
    public async Task<ActionResult<PipelineResult>> RunPipeline(
        [FromBody] PipelineRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("Nothing to do");
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        var picked = string.IsNullOrWhiteSpace(request.SubtitlePath)
            ? subtitles.Find(s => s.Supported)
            : subtitles.Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));

        if (picked is null)
        {
            return BadRequest("That subtitle doesn't belong to this item");
        }

        if (!picked.Supported)
        {
            return BadRequest("That's a picture based subtitle (PGS or VobSub). There's no text in it to work with - it needs OCR first.");
        }

        if (request.OutputFormat is not null && !SubtitleFormats.TryNormalizeOutputFormat(request.OutputFormat, out _))
        {
            return BadRequest("The output format has to be srt, vtt, ass or ssa");
        }

        var result = new PipelineResult { Extracted = picked.IsEmbedded };

        var (path, resolveError) = await ResolveToFileAsync(item, picked, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            result.Error = resolveError ?? "Could not get that subtitle out of the video file.";
            return result;
        }

        result.SubtitlePath = path;

        if (request.Sync)
        {
            var engine = _registry.Resolve(request.EngineId);
            var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

            if (!SupportsMode(engine, mode))
            {
                result.Error = $"{engine.Descriptor.DisplayName} doesn't support {mode} alignment";
                return result;
            }

            var sync = await _runner
                .RunAsync(
                    engine,
                    item.Path,
                    path,
                    mode,
                    EngineRunner.ResolvePenalty(engine, request.Penalty),
                    request.OutputMode,
                    outputFormat: request.OutputFormat,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            result.Sync = sync;
            result.ConvertedFrom = sync.ConvertedFrom;

            SyncQueueManager.SaveRecord(
                request.ItemId,
                ResolveRecordStatus(sync),
                sync.Success && sync.Skipped ? SyncQueueManager.DescribeSkip(sync) : sync.Error,
                sync,
                sync.Success && !sync.Skipped ? new[] { path } : null);

            if (!sync.Success)
            {
                result.Error = sync.Error;
                return result;
            }

            // Everything after this works on what the sync wrote, not on what went in.
            if (!string.IsNullOrEmpty(sync.OutputPath))
            {
                result.SubtitlePath = sync.OutputPath;
            }
        }
        else if (request.OutputFormat is not null
            && !string.Equals(SubtitleFormats.GetName(path), request.OutputFormat, StringComparison.OrdinalIgnoreCase))
        {
            // No sync asked for, so the conversion is the whole job here.
            SubtitleFormats.TryNormalizeOutputFormat(request.OutputFormat, out var target);
            var destination = Path.ChangeExtension(path, "." + target);

            try
            {
                await _converter.ConvertAsync(path, destination, cancellationToken).ConfigureAwait(false);
                result.ConvertedFrom = SubtitleFormats.GetName(path);
                result.SubtitlePath = destination;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        if (request.Translation is not null)
        {
            result.Translation = await _translationService
                .TranslateAsync(result.SubtitlePath, request.Translation, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Gets the formats a subtitle can be converted to.
    /// </summary>
    /// <returns>The format names.</returns>
    [HttpGet("Lapse/Convert/Formats")]
    public ActionResult<IReadOnlyList<string>> GetConvertFormats()
    {
        return Ok(SubtitleFormats.OutputFormats);
    }

    /// <summary>
    /// Syncs one subtitle file against another, always in standard mode.
    /// </summary>
    /// <param name="request">Reference and input subtitle paths.</param>
    /// <returns>The engine result.</returns>
    [HttpPost("Lapse/SyncSubtitles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Picking two arbitrary subtitle files is the whole point of this endpoint. It's admin only, and both paths are checked to be existing subtitle files first.")]
    public async Task<ActionResult<SyncResult>> SyncSubtitles([FromBody] SubtitleSyncRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ReferencePath) || string.IsNullOrWhiteSpace(request.InputPath))
        {
            return BadRequest("Both a reference and an input subtitle path are required");
        }

        // This feature is meant to point at any two files you like, so unlike the item
        // sync there's no library item to check them against. Still worth insisting both
        // are real subtitle files, so a typo can't turn this into "overwrite that file".
        if (!SubtitleLocator.IsSubtitleFile(request.ReferencePath) || !SubtitleLocator.IsSubtitleFile(request.InputPath))
        {
            return BadRequest("Both paths need to be subtitle files (.srt, .ass, .ssa or .vtt)");
        }

        if (!System.IO.File.Exists(request.ReferencePath) || !System.IO.File.Exists(request.InputPath))
        {
            return BadRequest("One of those subtitle files doesn't exist");
        }

        // Writing to a third file rather than over the input. Same rule as the paths going
        // in: it has to be a subtitle, and it can't be aimed at the reference, since that
        // would destroy the very file the sync is measuring against.
        var outputPath = string.IsNullOrWhiteSpace(request.OutputPath) ? null : request.OutputPath.Trim();

        // A bare file name means "use the configured placement", so the caller doesn't
        // have to know where that is.
        if (outputPath is not null && string.IsNullOrEmpty(Path.GetDirectoryName(outputPath)))
        {
            outputPath = Path.Combine(
                ResolveSubToSubFolder(request.ReferencePath, request.InputPath, request.Placement),
                outputPath);
        }

        if (outputPath is not null)
        {
            if (!SubtitleLocator.IsSubtitleFile(outputPath))
            {
                return BadRequest("The output file needs a subtitle extension (.srt, .ass, .ssa or .vtt)");
            }

            if (string.Equals(outputPath, request.ReferencePath, StringComparison.Ordinal))
            {
                return BadRequest("The output can't be the reference subtitle - that would overwrite what's being matched against");
            }

            var outputFolder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
            {
                return BadRequest("That output folder doesn't exist");
            }
        }

        var engine = _registry.Resolve(request.EngineId);
        var penalty = EngineRunner.ResolvePenalty(engine, null);
        var result = await _runner
            .RunAsync(engine, request.ReferencePath, request.InputPath, SyncMode.Standard, penalty, request.OutputMode, outputPath)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Gets a one line description of the OS and CPU the server is running on, so the
    /// dashboard can explain why a given engine has no download.
    /// </summary>
    /// <returns>The platform description.</returns>
    [HttpGet("Lapse/Platform")]
    public ActionResult<object> GetPlatform()
    {
        return new
        {
            Description = EngineInstaller.DetectedOsArch,
            IsWindows = OperatingSystem.IsWindows(),
            IsLinux = OperatingSystem.IsLinux(),
            IsMacOs = OperatingSystem.IsMacOS()
        };
    }

    /// <summary>
    /// Works out which folder a subtitle-to-subtitle result belongs in. Beside the
    /// reference is the default because the reference is the file that is already sitting
    /// correctly next to its video, so a result there is one Jellyfin will pick up as
    /// another track for that video. Beside the input leaves things where they were found,
    /// and a custom folder is for keeping the output out of the library entirely.
    /// </summary>
    /// <param name="referencePath">The reference subtitle.</param>
    /// <param name="inputPath">The subtitle being fixed.</param>
    /// <param name="requested">What the request asked for, or null for the configured default.</param>
    /// <returns>The folder to write into.</returns>
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

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // Only ever accept #rgb, #rrggbb or #rrggbbaa, so nothing that lands in a <style>
    // block on someone else's browser came out of a request body unchecked.
    private static string NormalizeColor(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        if (trimmed[0] != '#' || trimmed.Length is not (4 or 7 or 9))
        {
            return fallback;
        }

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                return fallback;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Finds one of an item's external subtitles by path, returning the library's own
    /// copy of the path rather than the caller's. Everything that writes to a subtitle
    /// goes through this, so a request can't name a file that isn't this item's.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="path">The path from the request.</param>
    /// <returns>The subtitle, or null.</returns>
    private SubtitleOption? FindSubtitle(Guid itemId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        return _subtitleLocator.GetExternalSubtitles(item)
            .Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));
    }

    // Turns a picked subtitle into something on disk. External ones already are; a track
    // still inside the video gets extracted next to it, which is a one-off - from then on
    // it's an ordinary sidecar that everything else here works on, and that Jellyfin picks
    // up as another track on its next scan.
    private Task<(string? Path, string? Error)> ResolveToFileAsync(
        BaseItem item,
        SubtitleOption option,
        CancellationToken cancellationToken)
    {
        return _extractor.ResolveAsync(item, option, cancellationToken);
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
