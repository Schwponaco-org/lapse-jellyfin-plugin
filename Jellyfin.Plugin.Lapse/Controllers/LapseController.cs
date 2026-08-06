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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// API endpoints for the LAPSE dashboard and the injected context menu.
/// Read-only endpoints just need a logged in user, anything that runs the engine or
/// touches disk needs an admin (RequiresElevation).
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class LapseController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly EngineInstaller _installer;
    private readonly EngineUpdater _updater;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly SubtitleShifter _subtitleShifter;
    private readonly TranslationService _translationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseController"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up items.</param>
    /// <param name="libraryService">Libraries and which of their items are eligible.</param>
    /// <param name="queueManager">Runs bulk/background sync jobs.</param>
    /// <param name="registry">The engines we know about.</param>
    /// <param name="runner">Runs single, synchronous engine calls.</param>
    /// <param name="installer">Downloads and installs engines.</param>
    /// <param name="updater">Checks for and applies engine updates.</param>
    /// <param name="subtitleLocator">Finds external subtitles for an item.</param>
    /// <param name="subtitleShifter">Nudges subtitle timings by hand.</param>
    /// <param name="translationService">Translates subtitle files.</param>
    public LapseController(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SyncQueueManager queueManager,
        EngineRegistry registry,
        EngineRunner runner,
        EngineInstaller installer,
        EngineUpdater updater,
        SubtitleLocator subtitleLocator,
        SubtitleShifter subtitleShifter,
        TranslationService translationService)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _queueManager = queueManager;
        _registry = registry;
        _runner = runner;
        _installer = installer;
        _updater = updater;
        _subtitleLocator = subtitleLocator;
        _subtitleShifter = subtitleShifter;
        _translationService = translationService;
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
        var libraryIds = _libraryService.GetLibraryIdSet();
        var result = new List<ItemStatusEntry>();

        foreach (var item in _libraryService.GetItems(includeSkipped: true))
        {
            var record = config.MovieRecords.FirstOrDefault(r => r.ItemId == item.Id);
            var status = SyncQueueManager.IsSkipped(item)
                ? MovieSyncStatus.Skipped
                : record?.Status ?? MovieSyncStatus.Pending;

            var libraryId = _libraryService.GetLibraryIdFor(item, libraryIds);

            result.Add(new ItemStatusEntry
            {
                ItemId = item.Id,
                Name = SyncQueueManager.DescribeItem(item),
                ItemType = item.GetBaseItemKind().ToString(),
                LibraryId = libraryId,
                LibraryName = libraryId.HasValue && libraryNames.TryGetValue(libraryId.Value, out var name) ? name : null,
                Status = status,
                LastSyncUtc = record?.LastSyncUtc,
                LastError = record?.LastError,
                SubtitleCount = _subtitleLocator.GetExternalSubtitles(item).Count
            });
        }

        return result;
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

    // ---------------------------------------------------------------------- syncing

    /// <summary>
    /// Syncs one item right now and waits for the result. Used by both the quick
    /// "Sync" popup and the advanced dialog, since a single sync is fast enough
    /// not to need the background queue.
    /// </summary>
    /// <param name="request">Which item, which mode, and which subtitle.</param>
    /// <returns>The engine result.</returns>
    [HttpPost("Lapse/Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SyncResult>> Sync([FromBody] SyncRequest request)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        if (subtitles.Count == 0)
        {
            return BadRequest("This item has no external subtitle to sync");
        }

        string subtitlePath;
        if (string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            if (subtitles.Count > 1)
            {
                return BadRequest("This item has more than one external subtitle - pick one with subtitlePath");
            }

            subtitlePath = subtitles[0].Path;
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

            subtitlePath = match.Path;
        }

        var engine = _registry.Resolve(request.EngineId);

        // Don't let a caller ask for something the chosen engine can't do. The dashboard
        // already greys these out, but the API shouldn't rely on the UI behaving.
        if (!SupportsMode(engine, request.Mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {request.Mode} alignment");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = await _runner
            .RunAsync(engine, item.Path, subtitlePath, request.Mode, penalty, request.OutputMode)
            .ConfigureAwait(false);

        SyncQueueManager.SaveRecord(
            request.ItemId,
            result.Success ? MovieSyncStatus.Synced : MovieSyncStatus.Failed,
            result.Error,
            result);

        return result;
    }

    /// <summary>
    /// Syncs every other subtitle on an item against one of them. Lining a subtitle up
    /// against another subtitle skips the audio decoding entirely, so this is both faster
    /// and more accurate than syncing each track to the video separately - as long as one
    /// of the tracks is known to be right.
    /// </summary>
    /// <param name="request">The item and which of its subtitles is the reference.</param>
    /// <returns>One result per non-reference subtitle.</returns>
    [HttpPost("Lapse/SyncAllSubtitles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<MultiSubtitleSyncResult>> SyncAllSubtitles([FromBody] MultiSubtitleSyncRequest request)
    {
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

        var others = subtitles.Where(s => !string.Equals(s.Path, reference.Path, StringComparison.Ordinal)).ToList();
        if (others.Count == 0)
        {
            return BadRequest("This item only has the one subtitle, so there's nothing to line up against it");
        }

        var engine = _registry.Resolve(request.EngineId);
        if (!SupportsMode(engine, request.Mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {request.Mode} alignment");
        }

        var penalty = EngineRunner.ResolvePenalty(engine, request.Penalty);
        var result = new MultiSubtitleSyncResult { ReferencePath = reference.Path };

        // Sequential on purpose: these engines are CPU heavy and running a handful of them
        // at once on a media server tends to make everything else stutter.
        foreach (var subtitle in others)
        {
            var syncResult = await _runner
                .RunAsync(engine, reference.Path, subtitle.Path, request.Mode, penalty, request.OutputMode)
                .ConfigureAwait(false);

            if (syncResult.Success)
            {
                result.SucceededCount++;
            }

            result.Results.Add(new SubtitleSyncOutcome
            {
                Path = subtitle.Path,
                DisplayName = subtitle.DisplayName,
                Result = syncResult
            });
        }

        SyncQueueManager.SaveRecord(
            request.ItemId,
            result.SucceededCount == others.Count ? MovieSyncStatus.Synced : MovieSyncStatus.Failed,
            result.SucceededCount == others.Count ? null : "Some subtitles failed to sync against the reference",
            result.Results.LastOrDefault()?.Result);

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
    /// Gets the current bulk sync queue progress, for the dashboard's progress bar.
    /// </summary>
    /// <returns>Queue snapshot.</returns>
    [HttpGet("Lapse/Queue")]
    public ActionResult<QueueSnapshot> GetQueue()
    {
        return _queueManager.GetSnapshot();
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

        foreach (var entry in request.Libraries)
        {
            var library = config.GetLibraryConfig(entry.ItemId);
            var wasScheduled = library.ScheduleEnabled;

            library.Enabled = entry.Enabled;
            library.ScheduleEnabled = entry.ScheduleEnabled;
            library.ScheduleDay = Enum.TryParse<DayOfWeek>(entry.ScheduleDay, out var day) ? day : null;
            library.ScheduleTime = string.IsNullOrWhiteSpace(entry.ScheduleTime) ? "03:00" : entry.ScheduleTime;

            if (names.TryGetValue(entry.ItemId, out var name))
            {
                library.Name = name;
            }

            // turning a schedule on should let it fire at the next slot, even if this
            // library ran within the last few hours under its old settings
            if (!wasScheduled && library.ScheduleEnabled)
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
    /// </summary>
    /// <returns>One entry per engine.</returns>
    [HttpGet("Lapse/Engines")]
    public async Task<ActionResult<List<EngineInfo>>> GetEngines()
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
                ? await _runner.GetRuntimeInfoAsync(engine).ConfigureAwait(false)
                : EngineRuntimeInfo.Unknown;

            result.Add(new EngineInfo
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Description = descriptor.Description,
                ProjectUrl = descriptor.ProjectUrl,
                BuildGuideUrl = descriptor.BuildGuideUrl,
                Installed = installed,
                Path = path,
                IsDefault = string.Equals(descriptor.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                Experimental = descriptor.Experimental,
                DownloadSupported = downloadable,
                NoDownloadReason = downloadable ? null : EngineInstaller.DescribeMissingBuild(engine),
                RunCheckError = installed ? await _runner.CheckRunnableAsync(engine).ConfigureAwait(false) : null,
                SupportsStandard = descriptor.Capabilities.SupportsStandard,
                SupportsOls = descriptor.Capabilities.SupportsOls,
                SupportsSplit = descriptor.Capabilities.SupportsSplit,
                SupportsPenalty = descriptor.Capabilities.SupportsPenalty,
                Penalty = EngineRunner.ResolvePenalty(engine, null),
                MinPenalty = descriptor.Capabilities.MinPenalty,
                MaxPenalty = descriptor.Capabilities.MaxPenalty,
                PathOverride = settings.PathOverride,
                InstalledVersion = installed ? settings.InstalledVersion : null,
                LatestVersion = settings.LatestKnownVersion,
                UpdateAvailable = installed
                    && downloadable
                    && GitHubReleaseClient.IsNewer(settings.InstalledVersion, settings.LatestKnownVersion),
                AutoUpdate = settings.AutoUpdate,
                ReportedVersion = runtime.Version,
                DiscoveredFlags = runtime.Probed ? runtime.Flags : null,
                CapabilitySource = runtime.Source,
                SupportsOutputFlag = runtime.SupportsOutputFlag,
                SupportsNoBackupFlag = runtime.SupportsNoBackupFlag
            });
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
    /// Turns auto-update on or off for one engine.
    /// </summary>
    /// <param name="engineId">Which engine.</param>
    /// <param name="enabled">Whether the daily task may update it.</param>
    /// <returns>Ok.</returns>
    [HttpPost("Lapse/Engines/{engineId}/AutoUpdate")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetEngineAutoUpdate([FromRoute] string engineId, [FromQuery] bool enabled)
    {
        var engine = _registry.Find(engineId);
        if (engine is null)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id).AutoUpdate = enabled;
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
            GoogleTranslateApiKey = config.GoogleTranslateApiKey,
            LingarrBaseUrl = config.LingarrBaseUrl,
            LingarrApiKey = config.LingarrApiKey,
            DefaultTranslationProvider = config.DefaultTranslationProvider,
            TranslationConfidenceThreshold = config.TranslationConfidenceThreshold,
            TranslationIncludeMetadataHeader = config.TranslationIncludeMetadataHeader,
            TranslationKeepLowConfidenceOriginal = config.TranslationKeepLowConfidenceOriginal
        };

        foreach (var engine in _registry.All)
        {
            var engineSettings = config.GetEngineSettings(engine.Descriptor.Id);
            settings.Engines.Add(new EngineSettingsEntry
            {
                EngineId = engine.Descriptor.Id,
                PathOverride = engineSettings.PathOverride,
                Penalty = engineSettings.Penalty,
                AutoUpdate = engineSettings.AutoUpdate
            });
        }

        return settings;
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
        config.GoogleTranslateApiKey = Blank(settings.GoogleTranslateApiKey);
        config.LingarrBaseUrl = Blank(settings.LingarrBaseUrl);
        config.LingarrApiKey = Blank(settings.LingarrApiKey);
        config.DefaultTranslationProvider = settings.DefaultTranslationProvider;
        config.TranslationConfidenceThreshold = Math.Clamp(settings.TranslationConfidenceThreshold, 0, 100);
        config.TranslationIncludeMetadataHeader = settings.TranslationIncludeMetadataHeader;
        config.TranslationKeepLowConfidenceOriginal = settings.TranslationKeepLowConfidenceOriginal;

        foreach (var entry in settings.Engines)
        {
            if (_registry.Find(entry.EngineId) is null)
            {
                continue;
            }

            var engineSettings = config.GetEngineSettings(entry.EngineId);
            engineSettings.PathOverride = Blank(entry.PathOverride);
            engineSettings.Penalty = entry.Penalty;
            engineSettings.AutoUpdate = entry.AutoUpdate;
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
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<TranslationResult>> Translate(
        [FromBody] TranslationRequest request,
        CancellationToken cancellationToken)
    {
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

        return await _translationService.TranslateAsync(match.Path, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the translation providers and whether each one is configured well enough to use.
    /// </summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("Lapse/Translate/Providers")]
    public ActionResult<List<object>> GetTranslationProviders()
    {
        var defaultProvider = Plugin.Instance!.Configuration.DefaultTranslationProvider;

        return _translationService.Providers
            .Select(p => (object)new
            {
                Id = p.Id.ToString(),
                p.DisplayName,
                Problem = p.GetConfigurationProblem(),
                IsDefault = p.Id == defaultProvider
            })
            .ToList();
    }

    // --------------------------------------------------------------- manual tinkering

    /// <summary>
    /// Nudges a subtitle file's timings by hand, for when a sync gets close but is still
    /// slightly off. Doesn't touch the engine, it just rewrites the timestamps.
    /// </summary>
    /// <param name="request">Which subtitle and how many seconds to move it.</param>
    /// <returns>Ok with how many timestamps changed.</returns>
    [HttpPost("Lapse/Shift")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Shift([FromBody] ShiftRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            return BadRequest("A subtitle path is required");
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        // Only ever write to a subtitle the library actually knows about for this item.
        // The path we hand to the shifter is the library's own copy, not the one from the
        // request body, so there's no way to talk this into editing some other file.
        var match = _subtitleLocator.GetExternalSubtitles(item)
            .Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));

        if (match is null)
        {
            return BadRequest("That subtitle doesn't belong to this item");
        }

        try
        {
            var shifted = await _subtitleShifter.ShiftAsync(match.Path, request.OffsetSeconds).ConfigureAwait(false);
            return Ok(new { Shifted = shifted });
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or IOException)
        {
            return BadRequest(ex.Message);
        }
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

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool SupportsMode(IEngine engine, SyncMode mode)
    {
        var capabilities = engine.Descriptor.Capabilities;
        return mode switch
        {
            SyncMode.Ols => capabilities.SupportsOls,
            SyncMode.Split => capabilities.SupportsSplit,
            _ => capabilities.SupportsStandard
        };
    }
}
