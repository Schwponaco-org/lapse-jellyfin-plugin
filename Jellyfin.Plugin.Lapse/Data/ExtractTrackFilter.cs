// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which embedded tracks the scheduled extraction takes, by whether they're forced.
/// </summary>
public enum ExtractTrackFilter
{
    /// <summary>
    /// Forced and full tracks alike.
    /// </summary>
    All,

    /// <summary>
    /// Only forced tracks: the ones that carry the lines in another language, signs and
    /// the like, for people who watch without subtitles otherwise.
    /// </summary>
    ForcedOnly,

    /// <summary>
    /// Everything except forced tracks.
    /// </summary>
    NotForced
}
