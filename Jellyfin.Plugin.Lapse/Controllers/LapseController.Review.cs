// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// The review list on the dashboard: every unsure answer still waiting, with a few places
/// to watch it at. Keeping or throwing one away goes through Candidates/Keep and Discard.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Gets every answer waiting on a decision, newest first, with spot check times in seconds.
    /// </summary>
    [HttpGet("Lapse/Review")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<List<object>>> GetReviewQueue(CancellationToken cancellationToken)
    {
        var queue = new List<object>();

        foreach (var set in MultiEngineSyncService.GetPending())
        {
            var item = GetVisibleItem(set.ItemId);
            var streams = item?.GetMediaStreams() ?? new List<MediaStream>();
            var candidates = new List<object>();

            foreach (var candidate in set.Candidates)
            {
                var spotChecks = await SubtitleShifter.ReadSpotChecksAsync(candidate.Path, candidate.PartStarts, cancellationToken).ConfigureAwait(false);

                candidates.Add(new
                {
                    candidate.EngineName,
                    candidate.Path,
                    candidate.Detail,
                    Exists = System.IO.File.Exists(candidate.Path),

                    // null until Jellyfin has scanned the file in, and the player can't show it before that
                    StreamIndex = (int?)streams.FirstOrDefault(s =>
                        s.Type == MediaStreamType.Subtitle
                        && string.Equals(s.Path, candidate.Path, StringComparison.OrdinalIgnoreCase))?.Index,
                    SpotChecks = spotChecks.ConvertAll(t => t.TotalSeconds)
                });
            }

            queue.Add(new
            {
                set.ItemId,
                ItemName = item is null ? set.ItemName : SyncQueueManager.DescribeItem(item),
                InLibrary = item is not null,
                set.OriginalPath,
                set.CreatedUtc,

                // the answers were timed from the file as it was, keeping one would undo whatever changed it
                OriginalChanged = System.IO.File.GetLastWriteTimeUtc(set.OriginalPath) > set.CreatedUtc,
                Candidates = candidates
            });
        }

        return queue;
    }
}
