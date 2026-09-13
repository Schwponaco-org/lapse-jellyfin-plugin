// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What an unattended run does about readable subtitles.
///
/// Off unless somebody asks for it. Rewriting every subtitle in a library is a decision
/// about everyone who uses it, not a default, and the two ways of doing it differ in
/// exactly that: one adds a track for whoever wants it, the other changes what everybody
/// else sees.
/// </summary>
public enum ReadableAutomationMode
{
    /// <summary>
    /// Leave subtitles alone. Readable copies are written when somebody presses the button
    /// on an item, and not otherwise.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Write a readable copy of every subtitle an automatic run touches, beside the
    /// original. Everyone keeps the subtitle they had, and there is a readable one in the
    /// list for whoever wants it.
    /// </summary>
    Copy = 1,

    /// <summary>
    /// Replace every subtitle an automatic run touches with its readable version, keeping
    /// the original as a backup. One track per language, styled. Only worth setting where
    /// everybody watching wants it.
    /// </summary>
    Replace = 2
}
