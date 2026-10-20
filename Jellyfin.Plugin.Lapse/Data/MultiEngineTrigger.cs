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
    /// Only when LAPSE says "unsure" (probably right but not by enough). That's where a second opinion pays off.
    /// </summary>
    UnsureOnly,

    /// <summary>
    /// "unsure" and "nothing". "nothing" is usually the wrong release, more files to sift for fewer wins.
    /// </summary>
    UnsureAndNothing
}
