// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One change to one subtitle file, kept so it can be undone with one button instead of digging
/// for the .bak in a shell.
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
    /// Gets or sets the subtitle the run read (same as <see cref="OutputPath"/> when overwritten). Null on old entries.
    /// </summary>
    public string? InputPath { get; set; }

    /// <summary>
    /// Gets or sets the backup taken before writing. Null means nothing to restore (no backup mode, or a new file was written).
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

    /// <summary>
    /// Gets or sets a value indicating whether a later run wrote the same file. Only the newest one can be undone.
    /// </summary>
    public bool Superseded { get; set; }
}
