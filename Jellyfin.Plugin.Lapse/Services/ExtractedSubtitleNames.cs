// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Names for extracted subtitles: Movie.eng.forced.stream8.srt. Jellyfin reads the
/// language and flags off that like any other sidecar, and the stream index keeps two
/// English tracks apart and makes the name fixed per track so we find it again.
///
/// Old versions wrote Movie.eng.track8.srt with Jellyfin's index rather than the file's,
/// so those can hold a different track than the number says. We leave them alone.
/// </summary>
public static partial class ExtractedSubtitleNames
{
    /// <summary>
    /// Max file name length in bytes (ext4/btrfs/XFS/ZFS count UTF-8 bytes, NTFS/APFS UTF-16 units, never more).
    /// </summary>
    public const int MaxFileNameBytes = 255;

    // Jellyfin reads these as flags, not languages, so a "language" like that becomes und
    private static readonly string[] ReservedTokens = { "forced", "foreign", "default", "sdh", "cc" };

    /// <summary>
    /// Turns a track's language tag into the file name part. The tag comes from the video
    /// and can be anything, so only code-shaped tags get through, the rest become "und".
    /// </summary>
    public static string LanguageTag(string? language)
    {
        var tag = language?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(tag) || tag.Length > 35 || !SafeTag().IsMatch(tag))
        {
            return "und";
        }

        // "hi" is Hindi but Jellyfin also reads it as the hearing impaired flag
        if (tag == "hi")
        {
            return "hin";
        }

        return Array.IndexOf(ReservedTokens, tag) >= 0 ? "und" : tag;
    }

    /// <summary>
    /// Builds the path an extracted track is written to.
    /// </summary>
    public static string BuildPath(
        string videoPath,
        int streamIndex,
        string? language,
        bool forced,
        bool hearingImpaired,
        string extension)
    {
        var folder = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(videoPath);

        var name = new StringBuilder(stem)
            .Append('.')
            .Append(LanguageTag(language));

        if (forced)
        {
            name.Append(".forced");
        }

        if (hearingImpaired)
        {
            name.Append(".sdh");
        }

        name.Append(".stream")
            .Append(streamIndex.ToString(CultureInfo.InvariantCulture))
            .Append(extension);

        return Path.Combine(folder, name.ToString());
    }

    /// <summary>
    /// Says whether the name fits (a long video name plus language and index can go over).
    /// </summary>
    public static bool FitsFileSystem(string path)
    {
        var name = Path.GetFileName(path);

        // the temp name is longer and has to fit too
        return Encoding.UTF8.GetByteCount(name) + TempSuffixLength <= MaxFileNameBytes;
    }

    /// <summary>
    /// Gets the length of what's added to a destination for the file written before it.
    /// </summary>
    public static int TempSuffixLength => ".lapse-extract-00000000".Length;

    /// <summary>
    /// Gets the temp file an extraction writes first. ".lapse-" hides it from our pickers
    /// and the non-subtitle extension hides it from Jellyfin's scanner.
    /// </summary>
    public static string TempPathFor(string destination)
    {
        return destination + ".lapse-extract-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Says whether a subtitle is one we extracted from this video, by name alone. Old
    /// style names don't count.
    /// </summary>
    public static bool IsExtractedTrack(string? videoPath, string? subtitlePath)
    {
        if (string.IsNullOrEmpty(videoPath) || string.IsNullOrEmpty(subtitlePath))
        {
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(videoPath), Path.GetDirectoryName(subtitlePath), StringComparison.Ordinal))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var name = Path.GetFileName(subtitlePath);

        if (name.Length <= stem.Length + 1
            || !name.StartsWith(stem, StringComparison.Ordinal)
            || name[stem.Length] != '.')
        {
            return false;
        }

        return ExtractedSuffix().IsMatch(name.AsSpan(stem.Length));
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTag();

    [GeneratedRegex(@"^\.[a-z0-9]+(-[a-z0-9]+)*(\.forced)?(\.sdh)?\.stream[0-9]{1,5}\.(srt|ass|ssa|vtt|sup)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtractedSuffix();
}
