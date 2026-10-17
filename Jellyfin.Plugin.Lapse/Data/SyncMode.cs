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
    /// One offset for the whole file. LAPSE "nosplit", alass --no-split, ffsubsync without a split penalty.
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
    /// Let the engine figure out shifted/drifting/split/re-cut by itself. LAPSE's default and ours.
    /// Last in the enum so old configs that stored numbers don't change meaning.
    /// </summary>
    Auto
}
