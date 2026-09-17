// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which of LAPSE's own verdicts sets the other engines running.
/// </summary>
public enum MultiEngineTrigger
{
    /// <summary>
    /// Only when LAPSE says "unsure": it found an answer and thinks it is probably right,
    /// but not by enough to overwrite the original on its own. This is the case where a
    /// second opinion is worth having, because there usually is a right answer to find.
    /// </summary>
    UnsureOnly,

    /// <summary>
    /// "unsure" and "nothing" both. "nothing" means the audio didn't back the answer up at
    /// all, which nearly always means the subtitle belongs to a different release. The
    /// other engines rarely rescue that, so this produces more files to sift through for
    /// fewer wins.
    /// </summary>
    UnsureAndNothing
}
