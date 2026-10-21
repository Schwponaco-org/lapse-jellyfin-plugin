// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Moving a subtitle from one copy of a film onto another (LAPSE 2.3.0's --from). The two
/// soundtracks say exactly how the copies differ, which is far more to go on than a subtitle
/// against speech: a TV recording against the Blu-ray, PAL against NTSC, a cut with a scene
/// missing.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Lists the other copies of an item a subtitle could be moved from.
    /// </summary>
    [HttpGet("Lapse/Items/{itemId}/Copies")]
    public async Task<ActionResult<CopyList>> GetCopies([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        var list = new CopyList { Problem = await CheckCanMoveAsync(cancellationToken).ConfigureAwait(false) };
        list.CanMove = list.Problem is null;

        foreach (var (copy, isVersion) in FindCopies(item))
        {
            list.Copies.Add(new CopyOption
            {
                ItemId = copy.Id,
                Name = System.IO.Path.GetFileNameWithoutExtension(copy.Path),
                Detail = isVersion
                    ? "Another version of this " + (item.GetBaseItemKind() == BaseItemKind.Episode ? "episode" : "film")
                    : "In " + (_libraryManager.GetCollectionFolders(copy).FirstOrDefault()?.Name ?? "another library"),
                SubtitleCount = _subtitleLocator.GetExternalSubtitles(copy).Count(s => s.Supported)
            });
        }

        return list;
    }

    /// <summary>
    /// Moves one of another copy's subtitles onto this item. The result is named after this
    /// item's video and never goes over a subtitle that's already there.
    /// </summary>
    [HttpPost("Lapse/SyncFromCopy")]
    public async Task<ActionResult<SyncResult>> SyncFromCopy([FromBody] CopySyncRequest request, CancellationToken cancellationToken)
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
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        // only a copy we'd have listed, so this can't reach files the caller can't see
        var source = FindCopies(item).Select(found => found.Copy).FirstOrDefault(copy => copy.Id == request.SourceItemId);
        if (source is null)
        {
            return BadRequest("That isn't another copy of this item");
        }

        var subtitle = _subtitleLocator.GetExternalSubtitles(source)
            .Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));
        if (subtitle is null)
        {
            return BadRequest("That subtitle doesn't belong to the copy you picked");
        }

        if (!subtitle.Supported)
        {
            return BadRequest("That's a picture based subtitle (PGS or VobSub). " + PictureSubtitleHint);
        }

        if (await CheckCanMoveAsync(cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return BadRequest(problem);
        }

        var engine = _registry.Find("lapse")!;
        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);

        var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);
        if (!SupportsMode(engine, mode))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} doesn't support {mode} alignment");
        }

        var (subtitlePath, resolveError) = await _extractor.ResolveAsync(source, subtitle, cancellationToken).ConfigureAwait(false);
        if (subtitlePath is null)
        {
            return BadRequest(resolveError ?? "Could not get that subtitle out of the video file.");
        }

        // a subtitle LAPSE can't read goes in as srt and comes out as srt, so name it that way
        var outputFormat = EngineRunner.ResolveConversionTarget(runtime, engine, subtitlePath);
        var destination = CopiedSubtitleNames.ResolveDestination(item.Path, source.Path, subtitlePath, subtitle.Language, outputFormat);

        var result = await _runner
            .RunAsync(
                engine,
                item.Path,
                subtitlePath,
                mode,
                EngineRunner.ResolvePenalty(engine, null),
                OutputMode.SidecarOnly,
                destination,
                outputFormat,
                fromVideoPath: source.Path,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // only the new file belongs to this item, the subtitle it came from is the other copy's
        SaveSyncRecord(item.Id, result, result.OutputPath is null ? Array.Empty<string>() : new[] { result.OutputPath });

        // always a new file, so Jellyfin needs a look before it lists it as a track
        if (result.Success && result.OutputPath is not null)
        {
            _multiEngine.RequestRefresh(item.Id);
        }

        return result;
    }

    // Only LAPSE can do it, and only from 2.3.0. The probe is cached, so this is cheap.
    private async Task<string?> CheckCanMoveAsync(CancellationToken cancellationToken)
    {
        if (_registry.Find("lapse") is not { } engine || !System.IO.File.Exists(_runner.ResolvePath(engine)))
        {
            return "Moving a subtitle between copies needs LAPSE. Install it from the LAPSE dashboard first.";
        }

        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        return runtime.HasFlag("--from")
            ? null
            : "Moving a subtitle between copies needs LAPSE 2.3.0 or newer. Update the engine from the LAPSE dashboard first.";
    }

    private List<(Video Copy, bool IsVersion)> FindCopies(BaseItem item)
    {
        return _libraryService.FindOtherCopies(item, User.IsInRole(AdministratorRole) ? null : GetCallingUser());
    }
}
