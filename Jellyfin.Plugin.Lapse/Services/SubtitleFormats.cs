// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// What kind of subtitle file something is, which decides what the plugin can do with it.
/// </summary>
public enum SubtitleFormatKind
{
    /// <summary>
    /// Not a subtitle file at all, or not one we know.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A format the plugin reads and writes itself, and the engines take directly:
    /// srt, vtt, ass and ssa.
    /// </summary>
    Native = 1,

    /// <summary>
    /// A text based format the plugin doesn't parse itself, but ffmpeg can turn into srt
    /// first - MicroDVD, SAMI, SubViewer, TTML and friends. Whether an engine needs that
    /// done is a separate question, and one only the engine can answer: LAPSE reads most
    /// of these directly. See <see cref="Engines.EngineFormats"/>.
    /// </summary>
    Convertible = 2,

    /// <summary>
    /// A picture based format - PGS, VobSub, DVD subs. There's no text in these at all,
    /// only bitmaps, so nothing here can convert, translate or shift one. Syncing is a
    /// different matter: LAPSE rewrites the timing without touching the pictures, so an
    /// engine that says it reads them can line one up.
    /// </summary>
    ImageBased = 3
}

/// <summary>
/// The subtitle formats the plugin knows about, and which of them each part of it can
/// work on. Everything that asks "can I do this to that file" comes through here, so
/// there's one answer rather than a list of extensions repeated in five places.
///
/// This is about what the <em>plugin</em> can do: read the text out, write it back,
/// translate it, shift it by hand. What an <em>engine</em> can do is asked of the engine,
/// in <see cref="Engines.EngineFormats"/>, because the answer differs per engine and per
/// installed build.
/// </summary>
public static class SubtitleFormats
{
    /// <summary>
    /// Formats the plugin parses and writes itself. These go straight to an engine and
    /// can be shifted, translated and converted between one another.
    /// </summary>
    public static readonly string[] NativeExtensions = { ".srt", ".vtt", ".ass", ".ssa" };

    /// <summary>
    /// Text based formats ffmpeg can read. The plugin converts these to srt on the way in
    /// rather than handing an engine something it would choke on.
    /// </summary>
    // No .txt or .xml here on purpose. Both turn up in media folders for reasons that
    // have nothing to do with subtitles - Jellyfin's own metadata among them - and a
    // scan that picked those up would offer people their nfo sidecars to sync.
    public static readonly string[] ConvertibleExtensions =
    {
        ".sub", ".smi", ".sami", ".sbv", ".ttml", ".dfxp", ".mpl2", ".jss", ".rt"
    };

    /// <summary>
    /// Picture based formats. Listed so the plugin can say why it can't help rather than
    /// pretending the file isn't there.
    /// </summary>
    public static readonly string[] ImageBasedExtensions = { ".sup", ".idx", ".pgs" };

    /// <summary>
    /// Gets the formats a converted file can be written as.
    /// </summary>
    public static IReadOnlyList<string> OutputFormats { get; } = new[] { "srt", "vtt", "ass", "ssa" };

    /// <summary>
    /// Works out what kind of subtitle a path is, from its extension.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The kind.</returns>
    public static SubtitleFormatKind GetKind(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return SubtitleFormatKind.Unknown;
        }

        var extension = Path.GetExtension(path);

        if (Matches(NativeExtensions, extension))
        {
            return SubtitleFormatKind.Native;
        }

        if (Matches(ImageBasedExtensions, extension))
        {
            return SubtitleFormatKind.ImageBased;
        }

        return Matches(ConvertibleExtensions, extension)
            ? SubtitleFormatKind.Convertible
            : SubtitleFormatKind.Unknown;
    }

    /// <summary>
    /// Gets the format name for a path, without the dot: "srt", "ass", and so on.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The lower case extension without its dot, or an empty string.</returns>
    public static string GetName(string? path)
    {
        var extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Gets whether a path is one of the formats the plugin handles itself.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True for srt, vtt, ass and ssa.</returns>
    public static bool IsNative(string? path)
    {
        return GetKind(path) == SubtitleFormatKind.Native;
    }

    /// <summary>
    /// Gets whether a path is a subtitle of any kind the plugin recognises, including the
    /// ones it can only convert or can't read at all.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True if it's a known subtitle format.</returns>
    public static bool IsSubtitle(string? path)
    {
        return GetKind(path) != SubtitleFormatKind.Unknown;
    }

    /// <summary>
    /// Gets whether a path holds text the plugin can get at, one way or another. This is
    /// what converting, translating and shifting by hand all need, and it's the line the
    /// picture based formats fall on the wrong side of.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True for the native and convertible formats.</returns>
    public static bool IsTextBased(string? path)
    {
        return GetKind(path) is SubtitleFormatKind.Native or SubtitleFormatKind.Convertible;
    }

    /// <summary>
    /// Checks that a requested output format is one that can actually be written.
    /// </summary>
    /// <param name="format">The format name, with or without a leading dot.</param>
    /// <param name="normalized">The cleaned up format name.</param>
    /// <returns>True if it's a format the converter writes.</returns>
    public static bool TryNormalizeOutputFormat(string? format, out string normalized)
    {
        normalized = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return OutputFormats.Contains(normalized, StringComparer.Ordinal);
    }

    private static bool Matches(string[] extensions, string extension)
    {
        foreach (var candidate in extensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
