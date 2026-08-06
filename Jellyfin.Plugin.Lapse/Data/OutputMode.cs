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
    /// This is what the plugin has always done, so it stays the default.
    /// </summary>
    OverwriteWithBackup,

    /// <summary>
    /// Replace the original subtitle and keep no copy.
    /// </summary>
    OverwriteNoBackup,

    /// <summary>
    /// Leave the original alone and write the synced result next to it as a new file,
    /// e.g. Movie.en.srt -> Movie.en.shifted.srt. Jellyfin picks the new file up as an
    /// extra subtitle track on its next scan.
    /// </summary>
    SidecarOnly,

    /// <summary>
    /// Same as <see cref="SidecarOnly"/>, but a .bak of any sidecar that was already
    /// there gets kept, so re-running a sync doesn't quietly discard the previous one.
    /// </summary>
    SidecarWithBackup
}
