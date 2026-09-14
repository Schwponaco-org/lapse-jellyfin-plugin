// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Asks for readable copies of an item's subtitles.
///
/// Which subtitles is always spelled out. A film can carry a Danish, an English and a
/// Spanish track, and somebody who wants the Danish one in a dyslexia-friendly font has
/// no reason to want the other two rewritten - still less if they share the library with
/// people reading those.
/// </summary>
public class RestyleRequest
{
    /// <summary>
    /// Gets or sets the item the subtitles belong to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the one subtitle to restyle. An embedded track is pulled out to a file
    /// first, same as everywhere else.
    /// </summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets the subtitles to restyle, when more than one was picked. Empty falls back to
    /// <see cref="SubtitlePath"/>.
    /// </summary>
    public List<string> SubtitlePaths { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the styled copy replaces the subtitle it
    /// came from. Off by default: the styled file is written beside the original as a new
    /// track, so both are offered in the player and everyone else's subtitle is left
    /// alone. On, the original is kept as a backup so this is still undoable.
    /// </summary>
    public bool ReplaceOriginal { get; set; }
}

/// <summary>
/// Asks for readable subtitles to be put back the way they were.
/// </summary>
public class RestyleRevertRequest
{
    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the readable subtitle to undo. Null undoes every readable subtitle on
    /// the item.
    /// </summary>
    public string? SubtitlePath { get; set; }
}

/// <summary>
/// What restyling one subtitle wrote.
/// </summary>
public class RestyleResult
{
    /// <summary>
    /// Gets or sets a value indicating whether a file was written.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the subtitle this came from.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the file that was written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the font the style asks for, after it was fitted to the script.
    /// </summary>
    public string? FontName { get; set; }

    /// <summary>
    /// Gets or sets the writing system the subtitle turned out to be in.
    /// </summary>
    public string? Script { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the configured font had to be swapped for
    /// one that can render this script. True means the size, outline and margin applied
    /// but the dyslexia typeface didn't, because it has no glyphs for these letters.
    /// </summary>
    public bool FontSwapped { get; set; }

    /// <summary>
    /// Gets or sets the letter spacing actually written, which is zero for scripts whose
    /// letters join up or carry stacked marks.
    /// </summary>
    public double LetterSpacing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file was marked as running right to
    /// left, which Arabic and Hebrew need and nothing else does.
    /// </summary>
    public bool RightToLeft { get; set; }

    /// <summary>
    /// Gets or sets how many cues came across.
    /// </summary>
    public int Cues { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this replaced the subtitle it came from.
    /// </summary>
    public bool Replaced { get; set; }

    /// <summary>
    /// Gets or sets where the original was kept, when this replaced it.
    /// </summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the font this asks for is actually where
    /// the player can find it. False means the file is correct but will render in
    /// whatever font the player falls back to.
    /// </summary>
    public bool FontAvailable { get; set; }

    /// <summary>
    /// Gets or sets why it didn't work, when it didn't.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// What a whole restyle request did, one entry per subtitle it was asked about.
/// </summary>
public class RestyleResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether at least one subtitle was written.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets the per-subtitle results.
    /// </summary>
    public List<RestyleResult> Files { get; } = new();
}

/// <summary>
/// What undoing one readable subtitle did.
/// </summary>
public class RestyleRevertItem
{
    /// <summary>
    /// Gets or sets the readable file that was undone.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it worked.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the original that was put back, when there was one to put back.
    /// Null means the readable file was an extra track and removing it was the whole job.
    /// </summary>
    public string? RestoredPath { get; set; }

    /// <summary>
    /// Gets or sets why it didn't work, when it didn't.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// What undoing a whole item's readable subtitles did.
/// </summary>
public class RestyleRevertResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether at least one file was put back.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets the per-file results.
    /// </summary>
    public List<RestyleRevertItem> Files { get; } = new();
}
