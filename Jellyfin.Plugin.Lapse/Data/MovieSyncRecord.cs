// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Sync history for one movie. This is what gets saved to the plugin's XML config
/// so we still know what's synced after a server restart.
/// </summary>
public class MovieSyncRecord
{
    /// <summary>
    /// Gets or sets the Jellyfin item id of the movie.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets how the last attempt went. How much is synced comes from <see cref="SyncedSubtitles"/>.
    /// </summary>
    public MovieSyncStatus Status { get; set; } = MovieSyncStatus.Pending;

    /// <summary>
    /// Gets the subtitle files that have actually been synced for this item.
    /// </summary>
    public List<SubtitleSyncRecord> SyncedSubtitles { get; } = new();

    /// <summary>
    /// Gets or sets when the last sync attempt happened, if any.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the error message from the last failed attempt, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets which mode the last sync used.
    /// </summary>
    public SyncMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the penalty used for the last sync, if it was a split sync.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets the constant offset (in milliseconds) from the last Standard sync.
    /// </summary>
    public int? OffsetMs { get; set; }

    /// <summary>
    /// Gets or sets the slope from the last Standard OLS sync.
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Gets or sets the intercept (in seconds) from the last Standard OLS sync.
    /// </summary>
    public double? Intercept { get; set; }

    /// <summary>
    /// Copies the record (with its own list), so it can be read outside <see cref="Plugin.ConfigurationLock"/>.
    /// </summary>
    public MovieSyncRecord Snapshot()
    {
        var copy = new MovieSyncRecord
        {
            ItemId = ItemId,
            Status = Status,
            LastSyncUtc = LastSyncUtc,
            LastError = LastError,
            Mode = Mode,
            Penalty = Penalty,
            OffsetMs = OffsetMs,
            Slope = Slope,
            Intercept = Intercept
        };

        copy.SyncedSubtitles.AddRange(SyncedSubtitles);
        return copy;
    }
}
