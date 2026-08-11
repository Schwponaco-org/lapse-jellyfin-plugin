// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Puts a sync back the way it was.
///
/// Two shapes, because there are two ways a sync can land. When it replaced a subtitle
/// there is a .bak sitting next to it and undoing means copying that back over. When it
/// wrote a new file next to the original - which is what the default output mode does -
/// nothing was replaced, so undoing means deleting the file it added and leaving the
/// original alone.
/// </summary>
public class SyncHistoryService
{
    private readonly ILogger<SyncHistoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHistoryService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SyncHistoryService(ILogger<SyncHistoryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Says whether an entry can still be undone. An entry whose backup has since been
    /// deleted, or whose output file is gone, is not offered as revertable rather than
    /// failing when somebody presses the button.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <returns>True if a revert would do something.</returns>
    public static bool CanRevert(SyncHistoryEntry entry)
    {
        if (entry.Reverted || string.IsNullOrEmpty(entry.OutputPath))
        {
            return false;
        }

        if (entry.WroteNewFile)
        {
            return File.Exists(entry.OutputPath);
        }

        return !string.IsNullOrEmpty(entry.BackupPath) && File.Exists(entry.BackupPath);
    }

    /// <summary>
    /// Undoes one sync.
    /// </summary>
    /// <param name="id">The history entry id.</param>
    /// <returns>A line saying what happened, or null when there was no such entry.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The paths are the plugin's own record of files it wrote itself, looked up by an opaque id. Nothing from the request reaches the filesystem.")]
    public string? Revert(Guid id)
    {
        var config = Plugin.Instance!.Configuration;
        var entry = config.History.FirstOrDefault(h => h.Id == id);

        if (entry is null)
        {
            return null;
        }

        if (!CanRevert(entry))
        {
            return entry.Reverted
                ? "That one has already been put back."
                : "There's nothing left to put back - the backup or the file it wrote has gone.";
        }

        try
        {
            if (entry.WroteNewFile)
            {
                File.Delete(entry.OutputPath!);
                _logger.LogInformation("Reverted a sync by removing {Path}", entry.OutputPath);
            }
            else
            {
                File.Copy(entry.BackupPath!, entry.OutputPath!, overwrite: true);
                File.Delete(entry.BackupPath!);
                _logger.LogInformation("Reverted a sync by restoring {Path}", entry.OutputPath);
            }

            entry.Reverted = true;
            ForgetSyncedSubtitle(entry);
            Plugin.Instance!.SaveConfiguration();

            return entry.WroteNewFile
                ? $"Deleted {Path.GetFileName(entry.OutputPath)}."
                : $"Put the original {Path.GetFileName(entry.OutputPath)} back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not revert the sync of {Path}", entry.OutputPath);
            return "Could not put it back: " + ex.Message;
        }
    }

    // The item's record still claims that subtitle is synced, which after a revert it
    // isn't. Dropping the claim puts the item back to unsynced in the status list rather
    // than leaving it looking done when the file on disk says otherwise.
    private static void ForgetSyncedSubtitle(SyncHistoryEntry entry)
    {
        var record = Plugin.Instance!.Configuration.MovieRecords
            .FirstOrDefault(r => r.ItemId == entry.ItemId);

        record?.SyncedSubtitles.RemoveAll(s =>
            string.Equals(s.Path, entry.OutputPath, StringComparison.Ordinal));
    }
}
