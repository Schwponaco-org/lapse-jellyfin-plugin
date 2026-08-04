// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which alignment mode to run the LAPSE engine in.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// Standard mode. One slope and intercept for the whole file. This is what the engine
    /// calls "OLS" (ordinary least squares) and what it does when penalty is 0 or left out.
    /// </summary>
    Ols,

    /// <summary>
    /// Split mode. Lets the engine break the subtitle into segments with their own timing.
    /// The penalty value controls how eager it is to add more splits.
    /// </summary>
    Split
}
