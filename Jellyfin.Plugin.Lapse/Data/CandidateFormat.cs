// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What format the candidate files get written in.
///
/// This exists because the engines do not read the same formats. LAPSE reads twelve,
/// alass and ffsubsync read four, so a subtitle in one of the other eight has to be
/// converted before the other two can say anything about it. This setting decides what
/// that conversion produces, and it only ever applies to the candidate files: nothing in
/// the library is converted or replaced by it.
/// </summary>
public enum CandidateFormat
{
    /// <summary>
    /// Keep each candidate as close to the original as the engine that wrote it allows.
    /// LAPSE writes the original format straight back. The other two get a converted copy
    /// to work on and their answer comes out as srt.
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
