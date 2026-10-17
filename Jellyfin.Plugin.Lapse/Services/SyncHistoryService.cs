// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Undoes syncs. If it replaced a subtitle, copy the .bak back. If it wrote a new file
/// next to the original (the default), just delete that file.
/// </summary>
public class SyncHistoryService
{
    private readonly ILogger<SyncHistoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHistoryService"/> class.
    /// </summary>
    public SyncHistoryService(ILogger<SyncHistoryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Says whether an entry can still be undone (its backup or output file is still there).
    /// </summary>
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
    public string? Revert(Guid id)
    {
        // the queue adds to these lists from its own thread
        lock (Plugin.ConfigurationLock)
        {
            return RevertLocked(id);
        }
    }

    /// <summary>
    /// Adds a history entry, marks earlier entries for the same file superseded and trims
    /// the list. Hold <see cref="Plugin.ConfigurationLock"/>.
    /// </summary>
    public static void Append(Configuration.PluginConfiguration config, SyncHistoryEntry entry)
    {
        foreach (var earlier in config.History)
        {
            if (earlier.Reverted || earlier.Superseded)
            {
                continue;
            }

            // same output or same .bak means only the newest can still be undone
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

    private string? RevertLocked(Guid id)
    {
        var config = Plugin.Instance!.Configuration;
        var entry = config.History.Find(h => h.Id == id);

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
                // The backup's name says where it belongs, which isn't always the written
                // file: readable can replace a .srt with an .ass, so restore the .srt and drop the .ass.
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

    // engine .bak and the readable restyle's own backup
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

    // After an undo the record shouldn't call the subtitle synced anymore. Both the input
    // and the output path get cleared since a sidecar run records both.
    private static void ForgetSyncedSubtitle(SyncHistoryEntry entry)
    {
        Plugin.Instance!.Configuration.FindRecord(entry.ItemId)?.SyncedSubtitles.RemoveAll(s =>
            SamePath(s.Path, entry.OutputPath) || SamePath(s.Path, entry.InputPath));
    }
}
