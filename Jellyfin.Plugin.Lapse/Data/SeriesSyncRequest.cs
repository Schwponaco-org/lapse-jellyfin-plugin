// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// A request to sync everything under one series or season.
/// </summary>
public class SeriesSyncRequest
{
    /// <summary>
    /// Gets or sets the series or season to work through.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the reference track key, from
    /// <see cref="Services.SeriesSyncService.GetReferenceOptions"/>. Null syncs every
    /// subtitle against its episode's audio instead.
    /// </summary>
    public string? ReferenceKey { get; set; }
}

/// <summary>
/// One subtitle track that exists across the episodes of a series, offered as a possible
/// reference to line the others up against.
/// </summary>
public class ReferenceOption
{
    /// <summary>
    /// Gets or sets the track key - a language code, or the suffix the file names carry.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many episodes actually have this track.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets how many episodes there are in total, so the dashboard can say
    /// "on 9 of 10 episodes" rather than a bare number.
    /// </summary>
    public int TotalEpisodes { get; set; }
}
