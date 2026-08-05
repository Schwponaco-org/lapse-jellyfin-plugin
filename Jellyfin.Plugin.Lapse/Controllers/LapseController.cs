// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// API endpoints for the LAPSE dashboard and the injected context menu button.
/// Read-only endpoints just need a logged in user, anything that runs the engine or
/// touches disk needs an admin (RequiresElevation).
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class LapseController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly SyncQueueManager _queueManager;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly EngineInstaller _installer;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly SubtitleShifter _subtitleShifter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseController"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up movies and folders.</param>
    /// <param name="queueManager">Runs bulk/background sync jobs.</param>
    /// <param name="registry">The engines we know about.</param>
    /// <param name="runner">Runs single, synchronous engine calls.</param>
    /// <param name="installer">Downloads and installs engines.</param>
    /// <param name="subtitleLocator">Finds external subtitles for a movie.</param>
    /// <param name="subtitleShifter">Nudges subtitle timings by hand.</param>
    public LapseController(
        ILibraryManager libraryManager,
        SyncQueueManager queueManager,
        EngineRegistry registry,
        EngineRunner runner,
        EngineInstaller installer,
        SubtitleLocator subtitleLocator,
        SubtitleShifter subtitleShifter)
    {
        _libraryManager = libraryManager;
        _queueManager = queueManager;
        _registry = registry;
        _runner = runner;
        _installer = installer;
        _subtitleLocator = subtitleLocator;
        _subtitleShifter = subtitleShifter;
    }

    /// <summary>
    /// Gets sync status for every movie in the library.
    /// </summary>
    /// <returns>One entry per movie.</returns>
    [HttpGet("Lapse/Status")]
    public ActionResult<List<MovieStatusEntry>> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var movies = GetAllMovies();
        var result = new List<MovieStatusEntry>();

        foreach (var movie in movies)
        {
            var record = config.MovieRecords.FirstOrDefault(r => r.ItemId == movie.Id);
            var status = SyncQueueManager.IsSkipped(movie)
                ? MovieSyncStatus.Skipped
                : record?.Status ?? MovieSyncStatus.Pending;

            result.Add(new MovieStatusEntry
            {
                ItemId = movie.Id,
                Name = movie.Name ?? "Unknown",
                Status = status,
                LastSyncUtc = record?.LastSyncUtc,
                LastError = record?.LastError,
                HasExternalSubtitle = _subtitleLocator.GetExternalSubtitles(movie).Count > 0
            });
        }

        return result;
    }

    /// <summary>
    /// Gets the external subtitle files for one movie, for the subtitle picker.
    /// </summary>
    /// <param name="itemId">The movie.</param>
    /// <returns>List of subtitle options.</returns>
    [HttpGet("Lapse/Movies/{itemId}/Subtitles")]
    public ActionResult<List<SubtitleOption>> GetMovieSubtitles([FromRoute] Guid itemId)
    {
        var movie = _libraryManager.GetItemById(itemId);
        if (movie is null)
        {
            return NotFound("Movie not found");
        }

        return _subtitleLocator.GetExternalSubtitles(movie);
    }

    /// <summary>
    /// Syncs one movie right now and waits for the result. Used by both the quick
    /// "Sync" popup and the advanced dialog, since a single movie sync is fast enough
    /// not to need the background queue.
    /// </summary>
    /// <param name="request">Which movie, which mode, and which subtitle.</param>
    /// <returns>The engine result.</returns>
    [HttpPost("Lapse/Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SyncResult>> Sync([FromBody] SyncRequest request)
    {
        var movie = _libraryManager.GetItemById(request.ItemId);
        if (movie is null || string.IsNullOrEmpty(movie.Path))
        {
            return NotFound("Movie not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(movie);
        if (subtitles.Count == 0)
        {
            return BadRequest("This movie has no external subtitle to sync");
        }

        string subtitlePath;
        if (string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            if (subtitles.Count > 1)
            {
                return BadRequest("This movie has more than one external subtitle - pick one with subtitlePath");
            }

            subtitlePath = subtitles[0].Path;
        }
        else
        {
            // Only ever write to a subtitle the library actually knows about for this
            // movie, and use the library's own copy of the path rather than the one from
            // the request, so this can't be talked into rewriting some other file.
            var match = subtitles.Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));
            if (match is null)
            {
                return BadRequest("That subtitle doesn't belong to this movie");
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
        var result = await _runner.RunAsync(engine, movie.Path, subtitlePath, request.Mode, penalty).ConfigureAwait(false);

        SyncQueueManager.SaveRecord(
            request.ItemId,
            result.Success ? MovieSyncStatus.Synced : MovieSyncStatus.Failed,
            result.Error,
            result);

        return result;
    }

    /// <summary>
    /// Starts a background job to sync every movie in the library, or just one folder.
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
            return Conflict("A sync job is already running, or there were no eligible movies to sync");
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
    /// Marks (or unmarks) a movie or folder as skipped.
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

    /// <summary>
    /// Gets the top level library folders, for the bulk sync folder dropdown.
    /// </summary>
    /// <returns>List of folders.</returns>
    [HttpGet("Lapse/Folders")]
    public ActionResult<List<FolderEntry>> GetFolders()
    {
        var config = Plugin.Instance!.Configuration;

        return _libraryManager.GetVirtualFolders()
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

    /// <summary>
    /// Gets every engine the plugin knows about, whether it's usable, and what it can do.
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

            result.Add(new EngineInfo
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Description = descriptor.Description,
                ProjectUrl = descriptor.ProjectUrl,
                Installed = installed,
                Path = path,
                IsDefault = string.Equals(descriptor.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                DownloadSupported = EngineRunner.GetDownloadUrl(engine) is not null,
                RunCheckError = installed ? await _runner.CheckRunnableAsync(engine).ConfigureAwait(false) : null,
                SupportsStandard = descriptor.Capabilities.SupportsStandard,
                SupportsOls = descriptor.Capabilities.SupportsOls,
                SupportsSplit = descriptor.Capabilities.SupportsSplit,
                SupportsPenalty = descriptor.Capabilities.SupportsPenalty,
                Penalty = EngineRunner.ResolvePenalty(engine, null),
                MinPenalty = descriptor.Capabilities.MinPenalty,
                MaxPenalty = descriptor.Capabilities.MaxPenalty,
                PathOverride = settings.PathOverride
            });
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
            await _installer.InstallAsync(engine).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
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
            if (EngineRunner.GetDownloadUrl(engine) is null)
            {
                results[engine.Descriptor.Id] = "skipped, no build for this architecture";
                continue;
            }

            try
            {
                await _installer.InstallAsync(engine).ConfigureAwait(false);
                results[engine.Descriptor.Id] = "installed";
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

        var movie = _libraryManager.GetItemById(request.ItemId);
        if (movie is null)
        {
            return NotFound("Movie not found");
        }

        // Only ever write to a subtitle the library actually knows about for this movie.
        // The path we hand to the shifter is the library's own copy, not the one from the
        // request body, so there's no way to talk this into editing some other file.
        var match = _subtitleLocator.GetExternalSubtitles(movie)
            .Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));

        if (match is null)
        {
            return BadRequest("That subtitle doesn't belong to this movie");
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

        // This feature is meant to point at any two files you like, so unlike the movie
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

        var engine = _registry.Resolve(request.EngineId);
        var penalty = EngineRunner.ResolvePenalty(engine, null);
        var result = await _runner.RunAsync(engine, request.ReferencePath, request.InputPath, SyncMode.Standard, penalty).ConfigureAwait(false);
        return result;
    }

    private List<BaseItem> GetAllMovies()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        };

        return _libraryManager.GetItemList(query).ToList();
    }
}
