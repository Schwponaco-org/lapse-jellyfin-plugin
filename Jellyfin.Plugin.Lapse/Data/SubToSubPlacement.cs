// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Where a subtitle-to-subtitle sync writes its result.
/// </summary>
public enum SubToSubPlacement
{
    /// <summary>
    /// Next to the reference subtitle (default), that one already sits next to its video.
    /// </summary>
    ReferenceFolder,

    /// <summary>
    /// Next to the input subtitle, leaving it where it was found.
    /// </summary>
    InputFolder,

    /// <summary>
    /// In a folder named in the settings.
    /// </summary>
    CustomFolder
}
