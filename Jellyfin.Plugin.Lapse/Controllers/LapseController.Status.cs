// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Status list, front page, an item's subtitles and some diagnostics.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Gets the sync status of every syncable item in the enabled libraries.
    /// </summary>
    [HttpGet("Lapse/Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<ItemStatusEntry>> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var libraryNames = _libraryService.GetLibraries().ToDictionary(l => l.ItemId, l => l.Name);
        var exclusions = ItemExclusions.Load();
        var countEmbedded = config.CountEmbeddedSubtitlesInStatus;

        // copy the records once up front instead of locking per item
        Dictionary<Guid, MovieSyncRecord> records;
        lock (Plugin.ConfigurationLock)
        {
            records = new Dictionary<Guid, MovieSyncRecord>(config.MovieRecords.Count);
            foreach (var entry in config.MovieRecords)
            {
                records.TryAdd(entry.ItemId, entry.Snapshot());
            }
        }

        var items = _libraryService.GetItemsWithLibrary(includeSkipped: true);
        var result = new List<ItemStatusEntry>(items.Count);

        foreach (var (item, libraryId) in items)
        {
            var record = records.GetValueOrDefault(item.Id);
            var subtitles = _subtitleLocator.GetExternalSubtitles(item);

            // Embedded tracks don't count unless asked for, nothing automatic syncs them.
            // An extracted track nobody synced is still the embedded track for this purpose.
            if (!countEmbedded)
            {
                subtitles = subtitles.FindAll(s => !s.IsEmbedded && !SubtitleExtractor.IsUntouchedExtraction(item, s.Path, record));
            }

            // files that were replaced since the sync don't count
            var syncedCount = record is null ? 0 : subtitles.Count(s => SyncQueueManager.IsStillSynced(record, s.Path));

            result.Add(new ItemStatusEntry
            {
                ItemId = item.Id,
                Name = SyncQueueManager.DescribeItem(item),
                ItemType = item.GetBaseItemKind().ToString(),
                LibraryId = libraryId,
                LibraryName = libraryNames.GetValueOrDefault(libraryId),
                Status = exclusions.Check(item) ?? ResolveDisplayStatus(record, subtitles.Count, syncedCount),
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
    /// Gets the front page bits the item list can't give: active engine and recent activity.
    /// Doesn't count the library on purpose, the browser already has the status list for that.
    /// </summary>
    [HttpGet("Lapse/Overview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<DashboardOverview> GetOverview()
    {
        var config = Plugin.Instance!.Configuration;
        var active = _registry.GetDefault();

        var overview = new DashboardOverview
        {
            AnyEngineInstalled = _registry.All.Any(e => System.IO.File.Exists(_runner.ResolvePath(e))),
            ActiveEngineId = active.Descriptor.Id,
            ActiveEngineName = active.Descriptor.DisplayName,
            ActiveEngineVersion = config.FindEngineSettings(active.Descriptor.Id)?.InstalledVersion,
            ActiveEngineMode = EngineRunner.ResolveDefaultMode(active).ToString(),
            ActiveEngineReady = System.IO.File.Exists(_runner.ResolvePath(active))
        };

        List<SyncHistoryEntry> recent;
        lock (Plugin.ConfigurationLock)
        {
            recent = config.History.TakeLast(15).Reverse().ToList();
        }

        foreach (var entry in recent)
        {
            var item = GetVisibleItem(entry.ItemId);

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
                Superseded = entry.Superseded,
                CanRevert = SyncHistoryService.CanRevert(entry)
            });
        }

        return overview;
    }

    /// <summary>
    /// Undoes one sync: puts the backup back or removes the file the run added.
    /// </summary>
    [HttpPost("Lapse/History/{id}/Revert")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RevertSync([FromRoute] Guid id)
    {
        var outcome = _historyService.Revert(id);
        return outcome is null ? NotFound("No history entry with that id") : Ok(new { Outcome = outcome });
    }

    // Worked out from which files are actually synced, not from a flag on the record. A flag
    // can't tell 1 of 4 tracks from all of them, or a stale record from an old install.
    private static MovieSyncStatus ResolveDisplayStatus(MovieSyncRecord? record, int subtitleCount, int syncedCount)
    {
        // a failure is worth showing even if an earlier run got some tracks done
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
    /// Gets an item's subtitles for the pickers.
    /// </summary>
    [HttpGet("Lapse/Items/{itemId}/Subtitles")]
    [HttpGet("Lapse/Movies/{itemId}/Subtitles")]
    public ActionResult<List<SubtitleOption>> GetItemSubtitles([FromRoute] Guid itemId)
    {
        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        return _subtitleLocator.GetExternalSubtitles(item);
    }

    /// <summary>
    /// Gets the first cue of a subtitle so the shift dialog can show a real line.
    /// </summary>
    [HttpGet("Lapse/Items/{itemId}/Subtitles/FirstCue")]
    public async Task<ActionResult<SubtitlePreview>> GetFirstCue(
        [FromRoute] Guid itemId,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        var (_, match) = FindSubtitle(itemId, path);
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

    /// <summary>
    /// Says whether our script is reaching the web client (no script, no context menu entries).
    /// </summary>
    [HttpGet("Lapse/Diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
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

    /// <summary>
    /// Gets the server's OS and CPU so the dashboard can explain a missing engine download.
    /// </summary>
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
}
