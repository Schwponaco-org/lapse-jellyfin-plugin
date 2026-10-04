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
        if (entry.Reverted || entry.Superseded || string.IsNullOrEmpty(entry.OutputPath))
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
        // The queue adds history and records from its own thread while this runs, and
        // the configuration can't be written out while a list in it is being changed.
        lock (Plugin.ConfigurationLock)
        {
            return RevertLocked(id);
        }
    }

    /// <summary>
    /// Adds an entry to the history, marks any earlier entry for the same file as
    /// superseded, and trims the list to its limit. Callers hold
    /// <see cref="Plugin.ConfigurationLock"/>.
    /// </summary>
    /// <param name="config">The configuration to add to.</param>
    /// <param name="entry">The new entry.</param>
    public static void Append(Configuration.PluginConfiguration config, SyncHistoryEntry entry)
    {
        foreach (var earlier in config.History)
        {
            if (earlier.Reverted || earlier.Superseded)
            {
                continue;
            }

            // Two runs that wrote the same file, or backed up to the same .bak, share the
            // one thing an undo works on. Only the newest of them still has it intact.
            if (SamePath(earlier.OutputPath, entry.OutputPath) || SamePath(earlier.BackupPath, entry.BackupPath))
            {
                earlier.Superseded = true;
            }
        }

        config.History.Add(entry);

        if (config.History.Count > Configuration.PluginConfiguration.MaxHistoryEntries)
        {
            config.History.RemoveRange(0, config.History.Count - Configuration.PluginConfiguration.MaxHistoryEntries);
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The paths are the plugin's own record of files it wrote itself, looked up by an opaque id. Nothing from the request reaches the filesystem.")]
    private string? RevertLocked(Guid id)
    {
        var config = Plugin.Instance!.Configuration;
        var entry = config.History.FirstOrDefault(h => h.Id == id);

        if (entry is null)
        {
            return null;
        }

        if (!CanRevert(entry))
        {
            return entry.Reverted ? "That one has already been put back."
                : entry.Superseded ? "A later run wrote the same file, so undo that one instead."
                : "There's nothing left to put back. The backup or the file it wrote has gone.";
        }

        var restored = entry.OutputPath;

        try
        {
            if (entry.WroteNewFile)
            {
                File.Delete(entry.OutputPath!);
                _logger.LogInformation("Reverted a sync by removing {Path}", entry.OutputPath);
            }
            else
            {
                // A backup is named after the file it was taken from, so that name is
                // where it belongs - which isn't always the file that was written. Making
                // a subtitle readable can replace a .srt with an .ass, and putting the
                // original back then means restoring the .srt and removing the .ass.
                var original = StripBackupSuffix(entry.BackupPath!) ?? entry.OutputPath!;

                File.Copy(entry.BackupPath!, original, overwrite: true);
                File.Delete(entry.BackupPath!);

                if (!string.Equals(original, entry.OutputPath, StringComparison.Ordinal)
                    && File.Exists(entry.OutputPath!))
                {
                    File.Delete(entry.OutputPath!);
                }

                _logger.LogInformation("Reverted by restoring {Path}", original);
                restored = original;
            }

            entry.Reverted = true;
            ForgetSyncedSubtitle(entry);
            Plugin.Instance!.SaveConfiguration();

            return entry.WroteNewFile
                ? $"Deleted {Path.GetFileName(entry.OutputPath)}."
                : $"Put {Path.GetFileName(restored)} back the way it was before that run.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not revert the sync of {Path}", entry.OutputPath);
            return "Could not put it back: " + ex.Message;
        }
    }

    // Both suffixes the plugin appends when it copies a file out of the way before writing
    // over it: an engine's .bak, and the readable restyle's own.
    private static string? StripBackupSuffix(string backupPath)
    {
        foreach (var suffix in new[] { ".bak", ReadableSubtitleService.BackupExtension })
        {
            if (backupPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return backupPath[..^suffix.Length];
            }
        }

        return null;
    }

    // The item's record still claims that subtitle is synced, which after a revert it
    // isn't. Dropping the claim puts the item back to unsynced in the status list rather
    // than leaving it looking done when the file on disk says otherwise. The record is
    // kept against the subtitle that was read, which for a run that wrote a new file
    // beside it isn't the output, so both are cleared.
    private static void ForgetSyncedSubtitle(SyncHistoryEntry entry)
    {
        var record = Plugin.Instance!.Configuration.MovieRecords
            .FirstOrDefault(r => r.ItemId == entry.ItemId);

        record?.SyncedSubtitles.RemoveAll(s =>
            SamePath(s.Path, entry.OutputPath) || SamePath(s.Path, entry.InputPath));
    }
}
