// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/Skip. Works for both movies and folders, they're all just item ids.
/// </summary>
public class SkipRequest
{
    /// <summary>
    /// Gets or sets the movie or folder to skip (or un-skip).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item should be skipped.
    /// </summary>
    public bool Skip { get; set; }
}
