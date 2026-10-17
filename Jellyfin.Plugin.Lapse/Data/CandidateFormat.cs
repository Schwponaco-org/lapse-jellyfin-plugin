// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Format of the multi engine candidate files. alass/ffsubsync read fewer formats than LAPSE so
/// some subtitles get converted for them. Only affects the candidates, never the library.
/// </summary>
public enum CandidateFormat
{
    /// <summary>
    /// As close to the original as each engine allows (LAPSE keeps the format, the others give srt).
    /// </summary>
    MatchOriginal,

    /// <summary>
    /// Write every candidate as srt, whatever came in. The format every player reads, and
    /// the one that is simplest to fix by hand afterwards.
    /// </summary>
    Srt,

    /// <summary>
    /// Write every candidate as ass. Worth picking when the original is ass or ssa, since
    /// srt would drop its styling on the way through.
    /// </summary>
    Ass
}
