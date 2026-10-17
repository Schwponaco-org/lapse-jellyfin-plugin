// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One subtitle file that got synced, and when. The item status is worked out from these instead
/// of a single flag that couldn't tell "all synced" from "1 of 4".
/// </summary>
public class SubtitleSyncRecord
{
    /// <summary>
    /// Gets or sets the subtitle file that was synced.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was synced.
    /// </summary>
    public DateTime LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the file's write time after the sync. Any other time later means it got replaced.
    /// Null on old records, which compare against <see cref="LastSyncUtc"/> instead.
    /// </summary>
    public DateTime? FileWriteUtc { get; set; }
}
