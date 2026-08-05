// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which alignment mode to run the LAPSE engine in. Maps directly to the engine's own
/// "nosplit" / "ols" / "split" mode argument.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// The default, one-click mode. Finds a single best constant offset for the whole
    /// file. This is the engine's own default when no mode is given (its "nosplit" mode).
    /// </summary>
    Standard,

    /// <summary>
    /// One slope and intercept for the whole file, fit with ordinary least squares. Labeled
    /// "Standard OLS" in the UI so it doesn't get confused with plain Standard.
    /// </summary>
    Ols,

    /// <summary>
    /// Lets the engine break the subtitle into segments with their own timing. The penalty
    /// value controls how eager it is to add more splits.
    /// </summary>
    Split
}
