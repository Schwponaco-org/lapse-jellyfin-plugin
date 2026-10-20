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
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// The in-player panel, OpenSubtitles fetching, and picking between multi engine answers.
/// </summary>
public partial class LapseController
{
    private const string OpenSubtitlesOff = "Fetching from OpenSubtitles is switched off.";

    /// <summary>
    /// Gets what the caller is watching: the item, the subtitle on screen and every subtitle
    /// the item has. Read from the session because the page only knows a stream index,
    /// and that works the same on every jellyfin-web version.
    /// </summary>
    [HttpGet("Lapse/Player/Context")]
    public ActionResult<object> GetPlayerContext()
    {
        var session = FindOwnSession(null);
        var item = session?.NowPlayingItem is { } playing ? GetVisibleItem(playing.Id) : null;

        if (session is null || item is null)
        {
            return new { Playing = false };
        }

        var streams = item.GetMediaStreams();
        var subtitles = _subtitleLocator.GetExternalSubtitles(item, streams);
        var playingPath = PlayingSubtitlePath(session, streams);

        // A file written since the last scan is syncable but has no stream index yet, so the
        // player can't switch to it. The panel needs to know which is which.
        var tracks = subtitles.ConvertAll(s => new
        {
            s.Path,
            s.DisplayName,
            s.Language,
            s.Format,
            s.Supported,
            s.TextBased,
            s.IsEmbedded,
            StreamIndex = (int?)streams.FirstOrDefault(ms =>
                ms.Type == MediaStreamType.Subtitle
                && !string.IsNullOrEmpty(ms.Path)
                && string.Equals(ms.Path, s.Path, StringComparison.OrdinalIgnoreCase))?.Index
        });

        var config = Plugin.Instance!.Configuration;

        return new
        {
            Playing = true,
            ItemId = item.Id,
            ItemName = SyncQueueManager.DescribeItem(item),
            PlayingPath = playingPath,

            // false when the track on screen is burnt in, embedded or off
            PlayingIsFile = playingPath is not null,
            Subtitles = tracks,
            CanEdit = CheckSubtitleAccess() is null,
            CanSync = System.IO.File.Exists(_runner.ResolvePath(_registry.GetDefault())),
            CanFetch = config.OpenSubtitlesEnabled,
            OutputMode = config.OutputMode.ToString(),
            Candidates = MultiEngineSyncService.GetPendingFor(item.Id)
        };
    }

    /// <summary>
    /// Asks Jellyfin to rescan an item's folder so a new subtitle file shows up as a track.
    /// </summary>
    [HttpPost("Lapse/Items/{itemId}/Rescan")]
    public ActionResult RescanItem([FromRoute] Guid itemId)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (GetVisibleItem(itemId) is null)
        {
            return NotFound("Item not found");
        }

        _multiEngine.RequestRefresh(itemId);
        return NoContent();
    }

    /// <summary>
    /// Fetches the best OpenSubtitles match for an item. Experimental.
    /// </summary>
    [HttpPost("Lapse/Items/{itemId}/FetchSubtitle")]
    public async Task<ActionResult<object>> FetchSubtitleForItem([FromRoute] Guid itemId)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (!Plugin.Instance!.Configuration.OpenSubtitlesEnabled)
        {
            return BadRequest(OpenSubtitlesOff);
        }

        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        var fetched = await _openSubtitles.TryFetchAsync(item, HttpContext.RequestAborted).ConfigureAwait(false);
        if (fetched.Path is null)
        {
            return new { Success = false, Message = fetched.Error ?? "Nothing was found for this one." };
        }

        // on disk but the player doesn't know yet
        _multiEngine.RequestRefresh(itemId);

        return new
        {
            Success = true,
            fetched.Path,
            Message = "Got " + Path.GetFileName(fetched.Path) + ". Give Jellyfin a few seconds to pick it up."
                + DescribeRemaining(fetched.RemainingDownloads)
        };
    }

    /// <summary>
    /// Searches OpenSubtitles for an item, best match first, so someone can pick one.
    /// </summary>
    [HttpGet("Lapse/Items/{itemId}/OpenSubtitles")]
    public async Task<ActionResult<OpenSubtitlesSearchResult>> SearchOpenSubtitles(
        [FromRoute] Guid itemId,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (!Plugin.Instance!.Configuration.OpenSubtitlesEnabled)
        {
            return BadRequest(OpenSubtitlesOff);
        }

        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return await _openSubtitles.SearchAsync(item, language, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one subtitle from a search, and syncs it straight away if asked.
    /// </summary>
    [HttpPost("Lapse/Items/{itemId}/OpenSubtitles/Download")]
    public async Task<ActionResult<object>> DownloadOpenSubtitle([FromRoute] Guid itemId, [FromBody] OpenSubtitlesDownloadRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || request.FileId <= 0)
        {
            return BadRequest("Say which subtitle to download");
        }

        if (!Plugin.Instance!.Configuration.OpenSubtitlesEnabled)
        {
            return BadRequest(OpenSubtitlesOff);
        }

        var item = GetVisibleItem(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var candidate = new OpenSubtitlesCandidate
        {
            FileId = request.FileId,
            Language = request.Language ?? string.Empty,
            FileName = request.FileName,
            HearingImpaired = request.HearingImpaired,
            ForeignPartsOnly = request.ForeignPartsOnly
        };

        var fetched = await _openSubtitles.DownloadAsync(item, candidate, HttpContext.RequestAborted).ConfigureAwait(false);
        if (fetched.Path is not { } path)
        {
            return new { Success = false, Message = fetched.Error ?? "Nothing was downloaded." };
        }

        // a downloaded subtitle is rarely timed for this exact file, that's why we exist
        SyncResult? sync = null;
        var engine = _registry.GetDefault();
        if (request.Sync && System.IO.File.Exists(_runner.ResolvePath(engine)))
        {
            sync = await _runner
                .RunAsync(engine, item.Path, path, EngineRunner.ResolveDefaultMode(engine), EngineRunner.ResolvePenalty(engine, null))
                .ConfigureAwait(false);

            SaveSyncRecord(item.Id, sync, path);
        }

        _multiEngine.RequestRefresh(itemId);

        return new
        {
            Success = true,
            Path = path,
            Sync = sync,
            Message = "Got " + Path.GetFileName(path) + "." + DescribeRemaining(fetched.RemainingDownloads)
        };
    }

    /// <summary>
    /// Tries the OpenSubtitles key and login so the settings page can say if they work.
    /// </summary>
    [HttpPost("Lapse/OpenSubtitles/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<OpenSubtitlesAccountStatus>> TestOpenSubtitles(
        [FromBody] OpenSubtitlesTestRequest? request,
        CancellationToken cancellationToken)
    {
        return await _openSubtitles.TestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets whether multi engine sync is usable plus everything waiting on a decision.
    /// </summary>
    [HttpGet("Lapse/MultiEngine")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetMultiEngineStatus()
    {
        return new
        {
            Availability = _multiEngine.GetAvailability(),
            Pending = MultiEngineSyncService.GetPending()
        };
    }

    /// <summary>
    /// Gets the answers waiting for one item and which one is on screen if the caller is watching it.
    /// </summary>
    [HttpGet("Lapse/Items/{itemId}/Candidates")]
    public ActionResult<object> GetItemCandidates([FromRoute] Guid itemId)
    {
        // these carry file paths, so hidden items get nothing
        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return new
        {
            Sets = MultiEngineSyncService.GetPendingFor(itemId),
            PlayingPath = PlayingSubtitlePath(FindOwnSession(itemId), item.GetMediaStreams())
        };
    }

    /// <summary>
    /// Gets what the caller is watching and whether the subtitle on screen is one of the
    /// waiting answers. People compare answers by switching tracks in the player, so the
    /// "keep this one" press needs to know which track is on.
    /// </summary>
    [HttpGet("Lapse/Candidates/Playing")]
    public ActionResult<object> GetPlayingCandidate()
    {
        var session = FindOwnSession(null);
        var item = session?.NowPlayingItem is { } playing ? GetVisibleItem(playing.Id) : null;

        if (session is null || item is null)
        {
            return new { Playing = false };
        }

        var playingPath = PlayingSubtitlePath(session, item.GetMediaStreams());
        var match = MultiEngineSyncService.MatchPlaying(item.Id, playingPath);

        return new
        {
            Playing = true,
            ItemId = item.Id,
            ItemName = session.NowPlayingItem!.Name,
            PlayingPath = playingPath,
            IsCandidate = match is not null,
            EngineName = match?.EngineName,
            Sets = MultiEngineSyncService.GetPendingFor(item.Id)
        };
    }

    /// <summary>
    /// Keeps one waiting answer and removes the rest. Without a path it keeps whatever
    /// track the caller is watching, which is the whole point: compare in the player, then
    /// one press keeps the one that lines up.
    /// </summary>
    [HttpPost("Lapse/Candidates/Keep")]
    public ActionResult<CandidateDecision> KeepCandidate([FromBody] CandidateRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || request.ItemId.Equals(default))
        {
            return BadRequest("An item id is required");
        }

        var path = request.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            var item = GetVisibleItem(request.ItemId);
            if (item is null)
            {
                return NotFound("Item not found");
            }

            path = PlayingSubtitlePath(FindOwnSession(item.Id), item.GetMediaStreams());

            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("Could not tell which subtitle you're watching. Pick one from the list instead.");
            }

            if (MultiEngineSyncService.MatchPlaying(request.ItemId, path) is null)
            {
                return BadRequest("The subtitle playing right now isn't one of the answers waiting to be chosen.");
            }
        }

        var decision = _multiEngine.Keep(request.ItemId, path);
        return decision.Success ? decision : BadRequest(decision.Message);
    }

    /// <summary>
    /// Throws away all waiting answers for an item and leaves the original alone.
    /// </summary>
    [HttpPost("Lapse/Candidates/Discard")]
    public ActionResult<CandidateDecision> DiscardCandidates([FromBody] CandidateRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || request.ItemId.Equals(default))
        {
            return BadRequest("An item id is required");
        }

        return _multiEngine.Discard(request.ItemId, request.OriginalPath);
    }

    private static string DescribeRemaining(int? remaining)
    {
        return remaining is { } left
            ? string.Create(CultureInfo.InvariantCulture, $" {left} OpenSubtitles download{(left == 1 ? string.Empty : "s")} left today.")
            : string.Empty;
    }

    // The caller's most recent session that's playing something (or that item, if given).
    // Any of their devices counts, so a press on the phone finds the film on the TV. Never
    // someone else's session though, that used to leak other accounts' file paths.
    private SessionInfo? FindOwnSession(Guid? itemId)
    {
        var user = GetCallingUser();
        if (user is null)
        {
            return null;
        }

        return _sessionManager.Sessions
            .Where(s => s.NowPlayingItem is not null
                && s.UserId.Equals(user.Id)
                && (itemId is null || s.NowPlayingItem.Id.Equals(itemId.Value)))
            .MaxBy(s => s.LastActivityDate);
    }

    // The subtitle file on screen in that session, or null for none/embedded/burnt in.
    private static string? PlayingSubtitlePath(SessionInfo? session, IReadOnlyList<MediaStream> streams)
    {
        if (session?.PlayState?.SubtitleStreamIndex is not { } index || index < 0)
        {
            return null;
        }

        return streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index)?.Path;
    }
}
