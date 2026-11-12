// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Undoes syncs. If it replaced a subtitle, copy the .bak back. If it wrote a new file
/// next to the original (the default), just delete that file. A whole bulk run can be
/// undone at once too.
/// </summary>
public class SyncHistoryService
{
    private readonly string _runsPath;
    private readonly ILogger<SyncHistoryService> _logger;
    private readonly object _runsLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHistoryService"/> class.
    /// </summary>
    public SyncHistoryService(IApplicationPaths applicationPaths, ILogger<SyncHistoryService> logger)
    {
        // A run lists every file it wrote, thousands for a library, so not in the config
        // that gets rewritten on every save.
        _runsPath = Path.Combine(applicationPaths.DataPath, "lapse", "runs.json");
        _logger = logger;
    }

    /// <summary>
    /// Gets how many finished runs are kept for undo.
    /// </summary>
    public static int MaxRuns => 5;

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
            var entry = Plugin.Instance!.Configuration.History.Find(h => h.Id == id);
            if (entry is null)
            {
                return null;
            }

            var outcome = RevertLocked(entry);
            if (entry.Reverted)
            {
                Plugin.Instance.SaveConfiguration();
            }

            return outcome;
        }
    }

    /// <summary>
    /// Keeps a finished run so it can be undone in one go. Only the last <see cref="MaxRuns"/> stay.
    /// </summary>
    public void SaveRun(BulkRun run)
    {
        lock (_runsLock)
        {
            var runs = LoadRuns();
            runs.Add(run);
            WriteRuns(runs.TakeLast(MaxRuns).ToList());
        }
    }

    /// <summary>
    /// Gets the kept runs, oldest first.
    /// </summary>
    public List<BulkRun> GetRuns()
    {
        lock (_runsLock)
        {
            return LoadRuns();
        }
    }

    /// <summary>
    /// Puts back every file a run wrote, carrying on past any that can't be. A file written
    /// since the run (a later sync, Bazarr, a hand edit) is left alone, its own Undo is still there.
    /// </summary>
    /// <returns>Each file and what happened to it, or null if there's no such run.</returns>
    public List<(SyncHistoryEntry File, string Outcome)>? UndoRun(Guid id)
    {
        lock (_runsLock)
        {
            var runs = LoadRuns();
            var run = runs.Find(r => r.Id == id);
            if (run is null)
            {
                return null;
            }

            var outcomes = new List<(SyncHistoryEntry File, string Outcome)>();

            foreach (var file in run.Files)
            {
                lock (Plugin.ConfigurationLock)
                {
                    // the history's own entry knows about single undos and later runs, while it's still there
                    var entry = Plugin.Instance!.Configuration.History.Find(h => h.Id == file.Id) ?? file;

                    var outcome = !entry.Reverted && !entry.Superseded && ChangedSince(entry)
                        ? "It has been written since the run, so it was left alone."
                        : RevertLocked(entry);

                    file.Reverted = entry.Reverted;
                    outcomes.Add((file, outcome));
                }
            }

            Plugin.Instance!.SaveConfiguration();
            WriteRuns(runs);
            return outcomes;
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

    private string RevertLocked(SyncHistoryEntry entry)
    {
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
                MultiEngineSyncService.Forget(entry.OutputPath!);
                _logger.LogInformation("Reverted a sync by removing {Path}", entry.OutputPath);
            }
            else
            {
                // The backup's name says where it belongs, which isn't always the written
                // file: readable can replace a .srt with an .ass, so restore the .srt and drop the .ass.
                var original = StripBackupSuffix(entry.BackupPath!) ?? entry.OutputPath!;

                File.Copy(entry.BackupPath!, original, overwrite: true);
                File.Delete(entry.BackupPath!);
                SubtitleFileLock.NoteWritten(original);

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

    // A file the run added that's gone anyway isn't a change, Revert says there's nothing left.
    private static bool ChangedSince(SyncHistoryEntry entry)
    {
        return entry.OutputWriteUtc is { } written
            && (File.Exists(entry.OutputPath)
                ? Math.Abs((File.GetLastWriteTimeUtc(entry.OutputPath) - written).TotalSeconds) >= 1
                : !entry.WroteNewFile);
    }

    private List<BulkRun> LoadRuns()
    {
        try
        {
            return File.Exists(_runsPath)
                ? JsonSerializer.Deserialize<List<BulkRun>>(File.ReadAllText(_runsPath)) ?? new List<BulkRun>()
                : new List<BulkRun>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the bulk runs from {Path}", _runsPath);
            return new List<BulkRun>();
        }
    }

    private void WriteRuns(List<BulkRun> runs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_runsPath)!);
            File.WriteAllText(_runsPath, JsonSerializer.Serialize(runs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the bulk runs to {Path}", _runsPath);
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
