// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Where a synced subtitle ends up, and whether the original is kept.
/// </summary>
public enum OutputMode
{
    /// <summary>
    /// Replace the original subtitle, keeping a .bak copy of what it looked like before.
    /// </summary>
    OverwriteWithBackup,

    /// <summary>
    /// Replace the original subtitle and keep no copy.
    /// </summary>
    OverwriteNoBackup,

    /// <summary>
    /// Leave the original alone and write next to it (Movie.en.srt -> Movie.en.shifted.srt). The default,
    /// it can't lose anyone's subtitle.
    /// </summary>
    SidecarOnly,

    /// <summary>
    /// Same as <see cref="SidecarOnly"/>, but a .bak of any sidecar that was already
    /// there gets kept, so re-running a sync doesn't quietly discard the previous one.
    /// </summary>
    SidecarWithBackup
}
