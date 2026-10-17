// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What unattended runs do about readable subtitles. Off by default, it changes what everyone sees.
/// </summary>
public enum ReadableAutomationMode
{
    /// <summary>
    /// Leave subtitles alone. Readable copies are written when somebody presses the button
    /// on an item, and not otherwise.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Add a readable copy next to every subtitle a run touches. Everyone keeps theirs.
    /// </summary>
    Copy = 1,

    /// <summary>
    /// Replace every subtitle a run touches with the readable one (original kept as backup). Only if everyone wants it.
    /// </summary>
    Replace = 2
}
