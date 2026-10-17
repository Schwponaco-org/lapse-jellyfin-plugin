// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What to do with a result the engine wasn't sure about. A low score usually means the subtitle
/// isn't for this video, and writing that over a good subtitle is the worst outcome.
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
