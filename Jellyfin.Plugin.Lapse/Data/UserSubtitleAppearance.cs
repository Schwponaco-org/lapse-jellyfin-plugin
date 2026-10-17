// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One user's subtitle appearance. On the server so it follows them between devices, unlike
/// Jellyfin's own per-device subtitle settings.
/// </summary>
public class UserSubtitleAppearance
{
    /// <summary>
    /// Gets or sets the user's id, as a dashless string, which is how the rest of the
    /// plugin's config stores user ids.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets their settings.
    /// </summary>
    public SubtitleAppearance Appearance { get; set; } = new();
}
