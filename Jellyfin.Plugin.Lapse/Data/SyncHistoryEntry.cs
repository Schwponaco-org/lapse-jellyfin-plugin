// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One thing that happened to one subtitle file, kept so it can be undone.
///
/// The plugin edits files in someone's library, which is the part of it worth being
/// nervous about. It already writes a .bak before overwriting anything, but nothing ever
/// pointed at those backups, so putting one back meant finding it in a shell. Recording
/// where the backup went next to what was written turns "it moved a subtitle that was
/// already fine" from a problem into one button.
/// </summary>
public class SyncHistoryEntry
{
    /// <summary>
    /// Gets or sets the id of this entry, so a revert can name one exactly.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the item this belonged to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets what to call the item, stored rather than looked up so an entry for
    /// something since removed from the library still reads sensibly.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it happened.
    /// </summary>
    public DateTime WhenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the engine that did it.
    /// </summary>
    public string? EngineId { get; set; }

    /// <summary>
    /// Gets or sets how it went.
    /// </summary>
    public MovieSyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the file that was written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the backup taken before writing, when the output mode asked for one.
    /// Null means there is nothing to put back - either the mode keeps no backup, or the
    /// run wrote a new file and left the original alone, in which case a revert is just
    /// deleting what it wrote.
    /// </summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the run wrote a new file rather than
    /// replacing one. Reverting one of those means removing the file it added.
    /// </summary>
    public bool WroteNewFile { get; set; }

    /// <summary>
    /// Gets or sets a short line about what the engine did.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this has already been undone.
    /// </summary>
    public bool Reverted { get; set; }
}
