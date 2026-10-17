// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Which libraries LAPSE works on and what it leaves alone.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Gets every library with its LAPSE settings.
    /// </summary>
    [HttpGet("Lapse/Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<LibraryEntry>> GetLibraries()
    {
        return _libraryService.GetLibraries();
    }

    /// <summary>
    /// Saves the per-library enable and schedule settings.
    /// </summary>
    [HttpPost("Lapse/Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveLibraries([FromBody] LibrarySettingsRequest request)
    {
        if (request?.Libraries is null || request.Libraries.Count == 0)
        {
            return BadRequest("No libraries were sent, so there was nothing to save");
        }

        var names = _libraryService.GetLibraries().ToDictionary(l => l.ItemId, l => l.Name);

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            foreach (var entry in request.Libraries)
            {
                var library = config.GetLibraryConfig(entry.ItemId);
                var wasScheduled = library.ScheduleEnabled;
                var oldFrequency = library.ScheduleFrequency;
                var oldDay = library.ScheduleDay;
                var oldTime = library.ScheduleTime;

                library.Enabled = entry.Enabled;

                // A disabled library stores both off, otherwise turning it back on later would
                // quietly bring back whatever was ticked before. And it's new items OR a
                // schedule, never both (the form enforces that too).
                library.AutoSyncEnabled = entry.Enabled && entry.AutoSyncEnabled;
                library.ScheduleEnabled = entry.Enabled && entry.ScheduleEnabled && !entry.AutoSyncEnabled;
                library.ScheduleFrequency = Enum.TryParse<ScheduleFrequency>(entry.ScheduleFrequency, out var frequency) ? frequency : ScheduleFrequency.Daily;
                library.ScheduleDay = Enum.TryParse<DayOfWeek>(entry.ScheduleDay, out var day) ? day : null;
                library.ScheduleTime = Blank(entry.ScheduleTime) ?? "03:00";

                if (names.TryGetValue(entry.ItemId, out var name))
                {
                    library.Name = name;
                }

                // a new or retimed schedule should fire at its next slot, not wait on the old last run
                var changed = library.ScheduleFrequency != oldFrequency
                    || library.ScheduleDay != oldDay
                    || !string.Equals(library.ScheduleTime, oldTime, StringComparison.Ordinal);

                if ((!wasScheduled && library.ScheduleEnabled) || changed)
                {
                    library.LastScheduledRunUtc = null;
                }
            }

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    /// <summary>
    /// Gets the top level library folders for the bulk sync dropdown.
    /// </summary>
    [HttpGet("Lapse/Folders")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<FolderEntry>> GetFolders()
    {
        return _libraryService.GetLibraries().ConvertAll(l => new FolderEntry
        {
            ItemId = l.ItemId,
            Name = l.Name,
            Skipped = l.Skipped
        });
    }

    /// <summary>
    /// Gets the ignore list, newest first.
    /// </summary>
    [HttpGet("Lapse/Ignore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<IgnoreRule>> GetIgnoreRules()
    {
        lock (Plugin.ConfigurationLock)
        {
            return Plugin.Instance!.Configuration.IgnoreRules.OrderByDescending(r => r.AddedUtc).ToList();
        }
    }

    /// <summary>
    /// Adds an item or a path to the ignore list.
    /// </summary>
    [HttpPost("Lapse/Ignore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult AddIgnoreRule([FromBody] IgnoreRule rule)
    {
        if (rule is null || (!rule.ItemId.HasValue && string.IsNullOrWhiteSpace(rule.Path)))
        {
            return BadRequest("An ignore rule needs either an item or a path");
        }

        IgnoreRule toAdd;
        if (rule.ItemId.HasValue)
        {
            var item = GetVisibleItem(rule.ItemId.Value);
            if (item is null)
            {
                return NotFound("Item not found");
            }

            toAdd = new IgnoreRule
            {
                ItemId = item.Id,
                DisplayName = SyncQueueManager.DescribeItem(item),
                Kind = item.GetBaseItemKind().ToString()
            };
        }
        else
        {
            var path = NormalizeIgnorePath(rule.Path!);
            toAdd = new IgnoreRule { Path = path, DisplayName = path, Kind = "Path" };
        }

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            var exists = config.IgnoreRules.Exists(r => toAdd.ItemId.HasValue
                ? r.ItemId == toAdd.ItemId
                : string.Equals(r.Path, toAdd.Path, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                config.IgnoreRules.Add(toAdd);
                Plugin.Instance.SaveConfiguration();
            }
        }

        return Ok();
    }

    /// <summary>
    /// Takes an item or path off the ignore list. Removed can be 0 when the item is only
    /// covered by a rule on its series or folder, the dashboard says so instead of looking broken.
    /// </summary>
    [HttpDelete("Lapse/Ignore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IgnoreRemovalResult> RemoveIgnoreRule([FromQuery] Guid? itemId, [FromQuery] string? path)
    {
        if (!itemId.HasValue && string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Say which item or path to take off the list");
        }

        // "/media/movies" and "/media/movies/" are the same rule
        var wantedPath = string.IsNullOrWhiteSpace(path) ? null : NormalizeIgnorePath(path);

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            var removed = config.IgnoreRules.RemoveAll(r =>
                (itemId.HasValue && r.ItemId == itemId)
                || (wantedPath is not null && r.Path is not null && string.Equals(NormalizeIgnorePath(r.Path), wantedPath, StringComparison.OrdinalIgnoreCase)));

            if (removed > 0)
            {
                Plugin.Instance.SaveConfiguration();
            }

            return new IgnoreRemovalResult { Removed = removed };
        }
    }

    // trims whitespace and a trailing slash (but leaves "/" or "C:\" alone)
    private static string NormalizeIgnorePath(string path)
    {
        var trimmed = path.Trim();
        var withoutSlash = trimmed.TrimEnd('/', '\\');
        return withoutSlash.Length == 0 || withoutSlash.EndsWith(':') ? trimmed : withoutSlash;
    }
}
