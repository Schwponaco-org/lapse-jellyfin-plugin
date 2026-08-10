// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What to do with a sync result the engine wasn't confident about. A low score usually
/// means the subtitle and the video aren't the same content, and writing that result over
/// a subtitle that was already fine is the one outcome nobody wants.
/// </summary>
public enum LowConfidenceAction
{
    /// <summary>
    /// Throw the result away and leave whatever was already there alone. The skip is
    /// logged and reported back to whoever asked for the sync.
    /// </summary>
    KeepOriginal,

    /// <summary>
    /// Write the result anyway, exactly as the output mode would have done.
    /// </summary>
    OverwriteAnyway,

    /// <summary>
    /// Write the result to a sidecar file, leaving the original where it is, whatever the
    /// output mode says.
    /// </summary>
    Sidecar
}
