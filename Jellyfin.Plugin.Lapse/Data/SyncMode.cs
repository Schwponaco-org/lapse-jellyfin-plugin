// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which alignment mode to run an engine in. The names are the plugin's; each engine
/// translates them into whatever its own command line calls the same thing.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// One constant offset for the whole file, no splitting. LAPSE calls this "nosplit",
    /// alass gets --no-split, and it's how ffsubsync behaves when it isn't given a split
    /// penalty.
    /// </summary>
    Standard,

    /// <summary>
    /// One slope and intercept for the whole file. LAPSE has this as its "ols" mode and
    /// nothing else here has a direct equivalent, so it stays a LAPSE-only option.
    /// </summary>
    Ols,

    /// <summary>
    /// Lets the engine break the subtitle into segments with their own timing. The penalty
    /// value controls how eager it is to add more splits.
    /// </summary>
    Split,

    /// <summary>
    /// Let the engine work out for itself whether the file is shifted, drifting, split,
    /// re-cut, or some combination, and handle each accordingly. This is what LAPSE does
    /// when it's given no mode at all, and it's what the plugin asks for by default.
    ///
    /// Deliberately last in the enum: the config is serialized to XML by name, but an
    /// older config that stored these as numbers would shift meaning if this went first.
    /// </summary>
    Auto
}
