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
    /// srt, vtt, ass, ssa. We read and write these ourselves.
    /// </summary>
    Native = 1,

    /// <summary>
    /// Text formats ffmpeg can turn into srt (MicroDVD, SAMI, TTML...). Whether an engine
    /// needs that is up to the engine, see <see cref="Engines.EngineFormats"/>.
    /// </summary>
    Convertible = 2,

    /// <summary>
    /// Pictures (PGS, VobSub). No text to convert, translate or shift, but LAPSE can still
    /// sync the timing.
    /// </summary>
    ImageBased = 3
}

/// <summary>
/// Which subtitle formats we know and what the plugin itself can do with them. What an
/// engine can read is a separate question, see <see cref="Engines.EngineFormats"/>.
/// </summary>
public static class SubtitleFormats
{
    /// <summary>
    /// Formats we parse and write ourselves.
    /// </summary>
    public static readonly string[] NativeExtensions = { ".srt", ".vtt", ".ass", ".ssa" };

    /// <summary>
    /// Text formats ffmpeg can read. No .txt or .xml on purpose, those are usually metadata.
    /// </summary>
    public static readonly string[] ConvertibleExtensions =
    {
        ".sub", ".smi", ".sami", ".sbv", ".ttml", ".dfxp", ".mpl2", ".jss", ".rt"
    };

    /// <summary>
    /// Picture formats, listed so we can explain why we can't help instead of hiding them.
    /// </summary>
    public static readonly string[] ImageBasedExtensions = { ".sup", ".idx", ".pgs" };

    /// <summary>
    /// Gets the formats a converted file can be written as.
    /// </summary>
    public static IReadOnlyList<string> OutputFormats { get; } = new[] { "srt", "vtt", "ass", "ssa" };

    /// <summary>
    /// Works out what kind of subtitle a path is, from its extension.
    /// </summary>
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
    public static string GetName(string? path)
    {
        var extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Gets whether a path is one of the formats the plugin handles itself.
    /// </summary>
    public static bool IsNative(string? path)
    {
        return GetKind(path) == SubtitleFormatKind.Native;
    }

    /// <summary>
    /// Gets whether a path is any subtitle format we recognise.
    /// </summary>
    public static bool IsSubtitle(string? path)
    {
        return GetKind(path) != SubtitleFormatKind.Unknown;
    }

    /// <summary>
    /// Gets whether we can get text out of it (needed to convert, translate or shift).
    /// </summary>
    public static bool IsTextBased(string? path)
    {
        return GetKind(path) is SubtitleFormatKind.Native or SubtitleFormatKind.Convertible;
    }

    /// <summary>
    /// Checks that a requested output format is one that can actually be written.
    /// </summary>
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
