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
/// What an extracted subtitle is called, and how to recognise one again later.
///
/// The name is the video's own, then the language, then forced and sdh where they apply,
/// then the stream's index in the file: Movie.eng.forced.stream8.srt. Jellyfin reads the
/// language and both flags off that exactly as it would off a subtitle anyone else put
/// there, and the index keeps two English tracks from landing on the same file. It also
/// makes the name a fixed answer for a given track, so extracting the same track twice
/// finds the first file instead of making a second.
///
/// Older versions wrote Movie.eng.track8.srt, and the number in those is Jellyfin's
/// rather than the file's. The two differ as soon as there's any subtitle file beside the
/// video, so an old name can hold a different track than its number says. Those files
/// are never taken as an extraction of anything: they're left exactly as they are, and
/// the new name can't be mistaken for one of them.
/// </summary>
public static partial class ExtractedSubtitleNames
{
    /// <summary>
    /// The longest file name, in bytes, the common filesystems take. ext4, btrfs, XFS and
    /// ZFS count UTF-8 bytes; NTFS and APFS count UTF-16 units, which is never more.
    /// </summary>
    public const int MaxFileNameBytes = 255;

    // What Jellyfin treats as a flag rather than a language when it reads a subtitle's
    // name. A track tagged with one of these as its "language" would come out named as a
    // flag, so those go down as und instead.
    private static readonly string[] ReservedTokens = { "forced", "foreign", "default", "sdh", "cc" };

    /// <summary>
    /// Turns a track's language tag into the piece of the file name that carries it.
    ///
    /// The tag comes out of the video file, and a video file can say anything: a path, a
    /// sentence, nothing. Only something shaped like a language code - letters and digits,
    /// optionally a region after a dash - goes into a file name. Anything else is written
    /// as und, which Jellyfin shows as an unknown language rather than trips over.
    /// </summary>
    /// <param name="language">The tag.</param>
    /// <returns>A safe, lower case tag.</returns>
    public static string LanguageTag(string? language)
    {
        var tag = language?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(tag) || tag.Length > 35 || !SafeTag().IsMatch(tag))
        {
            return "und";
        }

        // "hi" is Hindi, but it's also the hard of hearing flag, and next to .sdh Jellyfin
        // reads it as the flag. The three letter code means only the one thing.
        if (tag == "hi")
        {
            return "hin";
        }

        return Array.IndexOf(ReservedTokens, tag) >= 0 ? "und" : tag;
    }

    /// <summary>
    /// Builds the path an extracted track is written to.
    /// </summary>
    /// <param name="videoPath">The video the track comes from.</param>
    /// <param name="streamIndex">The track's stream index.</param>
    /// <param name="language">The track's language tag.</param>
    /// <param name="forced">Whether the track is forced.</param>
    /// <param name="hearingImpaired">Whether the track is SDH.</param>
    /// <param name="extension">The extension, dot included.</param>
    /// <returns>The path, beside the video.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Built from Jellyfin's own resolved video path, a language tag cut down to letters, digits and dashes, an integer and a fixed extension.")]
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
    /// Says whether a file name is short enough for the filesystems a library lives on.
    /// A video with a very long name already sits near the limit, and the language and
    /// track index on the end can push its subtitle over.
    /// </summary>
    /// <param name="path">The path whose last part is checked.</param>
    /// <returns>True when it fits.</returns>
    public static bool FitsFileSystem(string path)
    {
        var name = Path.GetFileName(path);

        // The temporary file ffmpeg writes first is longer than the final name, and it
        // has to fit too.
        return Encoding.UTF8.GetByteCount(name) + TempSuffixLength <= MaxFileNameBytes;
    }

    /// <summary>
    /// Gets the length of what's added to a destination for the file written before it.
    /// </summary>
    public static int TempSuffixLength => ".lapse-extract-00000000".Length;

    /// <summary>
    /// Gets the temporary file an extraction writes before it's moved into place. It
    /// carries .lapse- so nothing in the plugin offers it as a subtitle, and it doesn't
    /// end in a subtitle extension, so Jellyfin's scanner ignores it too.
    /// </summary>
    /// <param name="destination">Where the finished file goes.</param>
    /// <returns>The temporary path.</returns>
    public static string TempPathFor(string destination)
    {
        return destination + ".lapse-extract-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Says whether a subtitle file is one LAPSE extracted from this video, going by its
    /// name alone: the video's name, a language tag, optional forced/sdh, a stream number.
    /// Names from older versions don't count, so those files go on being treated exactly
    /// as they were before.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>True when it has the name an extraction gives.</returns>
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
