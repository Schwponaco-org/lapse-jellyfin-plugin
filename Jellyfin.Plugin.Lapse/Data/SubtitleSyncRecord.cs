// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One subtitle file that has actually been synced, and when.
///
/// The status used to be a single flag on the item, which couldn't tell "synced" from
/// "one of its four subtitles was synced", and couldn't tell either of those from a
/// record left behind by an older install. Recording the files themselves means the
/// status is worked out from evidence rather than asserted.
/// </summary>
public class SubtitleSyncRecord
{
    /// <summary>
    /// Gets or sets the subtitle file that was synced.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was synced.
    /// </summary>
    public DateTime LastSyncUtc { get; set; }
}
