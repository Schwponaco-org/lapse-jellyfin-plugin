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
using Jellyfin.Plugin.Lapse.Engine;
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
    private readonly LapseEngineClient _engineClient;
    private readonly EngineDownloadService _downloadService;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly SubtitleShifter _subtitleShifter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseController"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to look up movies and folders.</param>
    /// <param name="queueManager">Runs bulk/background sync jobs.</param>
    /// <param name="engineClient">Runs single, synchronous engine calls.</param>
    /// <param name="downloadService">Handles engine binary status/download.</param>
    /// <param name="subtitleLocator">Finds external subtitles for a movie.</param>
    /// <param name="subtitleShifter">Nudges subtitle timings by hand.</param>
    public LapseController(
        ILibraryManager libraryManager,
        SyncQueueManager queueManager,
        LapseEngineClient engineClient,
        EngineDownloadService downloadService,
        SubtitleLocator subtitleLocator,
        SubtitleShifter subtitleShifter)
    {
        _libraryManager = libraryManager;
        _queueManager = queueManager;
        _engineClient = engineClient;
        _downloadService = downloadService;
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

        var subtitlePath = request.SubtitlePath;
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            var subtitles = _subtitleLocator.GetExternalSubtitles(movie);
            if (subtitles.Count == 0)
            {
                return BadRequest("This movie has no external subtitle to sync");
            }

            if (subtitles.Count > 1)
            {
                return BadRequest("This movie has more than one external subtitle - pick one with subtitlePath");
            }

            subtitlePath = subtitles[0].Path;
        }

        var result = await _engineClient.RunAsync(movie.Path, subtitlePath, request.Mode, request.Penalty).ConfigureAwait(false);

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
    /// Gets whether the engine binary is downloaded and ready to use.
    /// </summary>
    /// <returns>Engine status.</returns>
    [HttpGet("Lapse/Engine/Status")]
    public async Task<ActionResult<EngineStatus>> GetEngineStatus()
    {
        return await _downloadService.GetStatusAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the LAPSE engine binary for this server's OS/architecture.
    /// </summary>
    /// <returns>Ok, or a 400 explaining why it couldn't be downloaded.</returns>
    [HttpPost("Lapse/Engine/Download")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> DownloadEngine()
    {
        try
        {
            await _downloadService.DownloadAsync().ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
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
    /// Syncs one subtitle file against another, always in standard (OLS) mode.
    /// </summary>
    /// <param name="request">Reference and input subtitle paths.</param>
    /// <returns>The engine result.</returns>
    [HttpPost("Lapse/SyncSubtitles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SyncResult>> SyncSubtitles([FromBody] SubtitleSyncRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReferencePath) || string.IsNullOrWhiteSpace(request.InputPath))
        {
            return BadRequest("Both a reference and an input subtitle path are required");
        }

        var result = await _engineClient.RunSubtitleToSubtitleAsync(request.ReferencePath, request.InputPath).ConfigureAwait(false);
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
