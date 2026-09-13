// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One user's own subtitle appearance, kept against their id.
///
/// Stored on the server rather than in the browser so it follows the person from the TV
/// to the phone to the laptop, which is the whole complaint about Jellyfin's own subtitle
/// settings: they are per device, so anyone who needs them has to find and set them again
/// everywhere, on clients that mostly have no font picker at all.
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
