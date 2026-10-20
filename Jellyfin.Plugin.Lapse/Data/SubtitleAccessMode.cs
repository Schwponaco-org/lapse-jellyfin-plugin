// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Who besides admins may sync, shift, convert or translate an item's subtitles. Plugin settings stay admin only regardless.
/// </summary>
public enum SubtitleAccessMode
{
    /// <summary>
    /// Administrators only. The default: syncing rewrites files in the library, so nobody
    /// gets to do it until an admin says who.
    /// </summary>
    AdminsOnly = 0,

    /// <summary>
    /// Users with Jellyfin's subtitle management permission. They can already download subtitles anyway.
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
