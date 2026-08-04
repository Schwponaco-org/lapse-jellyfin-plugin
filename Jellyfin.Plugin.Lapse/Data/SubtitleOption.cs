// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One external subtitle file that could be synced, shown in the subtitle picker
/// when a movie has more than one.
/// </summary>
public class SubtitleOption
{
    /// <summary>
    /// Gets or sets the full path to the subtitle file on disk.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human readable label for the picker, usually the language plus the file name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subtitle language, if Jellyfin knows it.
    /// </summary>
    public string? Language { get; set; }
}
