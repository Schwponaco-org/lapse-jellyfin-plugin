// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Who, apart from administrators, may work on an item's subtitles: sync it, shift it,
/// convert it or translate it. Administrators always may, and everything that changes the
/// plugin itself - settings, engines, libraries, bulk runs - stays admin only whatever
/// this is set to.
/// </summary>
public enum SubtitleAccessMode
{
    /// <summary>
    /// Administrators only. The default: syncing rewrites files in the library, so nobody
    /// gets to do it until an admin says who.
    /// </summary>
    AdminsOnly = 0,

    /// <summary>
    /// Anyone holding Jellyfin's own "subtitle management" permission. Those users can
    /// already download subtitle files into the library, so this hands them nothing they
    /// couldn't do a messier version of already.
    /// </summary>
    SubtitleManagers = 1,

    /// <summary>
    /// Only the users the admin picked by name.
    /// </summary>
    SelectedUsers = 2,

    /// <summary>
    /// Every signed in user.
    /// </summary>
    Everyone = 3
}
