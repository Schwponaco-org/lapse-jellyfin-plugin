// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Finds every subtitle an item has: the files sitting next to the video, and the tracks
/// still inside the video itself.
///
/// The engines only read files, so an embedded track can't be handed to one as it stands.
/// It's still listed, because on most libraries embedded is all there is and a plugin
/// that says "no subtitle found" to a film with eleven of them is no use to anybody. What
/// happens instead is that the track gets pulled out to a file beside the video the first
/// time something needs it. See <see cref="SubtitleExtractor"/>.
/// </summary>
public class SubtitleLocator
{
    private static readonly TimeSpan FolderCacheFor = TimeSpan.FromSeconds(15);

    // Where Jellyfin and every subtitle tool put subtitles when they don't put them next
    // to the video. Searched as well as the video's own folder, since a subtitle sitting
    // in one of these is still that video's subtitle.
    private static readonly string[] SubtitleSubfolders = { "Subs", "Subtitles", "subs", "subtitles" };

    // The dashboard's item list asks about every item in every enabled library, and a TV
    // library puts a whole season in one folder, so without this the same directory gets
    // enumerated once per episode. Short lived on purpose: long enough to cover one page
    // load, short enough that a subtitle dropped in a folder still shows up promptly.
    private readonly ConcurrentDictionary<string, (string[] Files, DateTime ReadUtc)> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets every external subtitle file for the given item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>List of subtitle options, empty if there aren't any external subs.</returns>
    public List<SubtitleOption> GetExternalSubtitles(BaseItem item)
    {
        var options = new List<SubtitleOption>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in item.GetMediaStreams())
        {
            // A subtitle stream with a file path behind it is an external subtitle,
            // whatever the IsExternal flag says. That flag isn't always set the way you'd
            // expect - a subtitle Jellyfin picked up from a Subs folder, or one written by
            // another plugin, can come back with a perfectly good path and IsExternal
            // false - and insisting on it was hiding files the server had clearly found.
            // An embedded stream has no path at all, so this still can't confuse the two.
            if (stream.Type != MediaStreamType.Subtitle || string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            if (!SubtitleFormats.IsSubtitle(stream.Path))
            {
                continue;
            }

            if (!seenPaths.Add(stream.Path))
            {
                continue;
            }

            var fileName = Path.GetFileName(stream.Path);
            var displayName = string.IsNullOrEmpty(stream.Language) ? fileName : $"{stream.Language} ({fileName})";

            options.Add(Describe(stream.Path, displayName, stream.Language));
        }

        AddSubtitlesFromDisk(item, options, seenPaths);
        AddEmbeddedSubtitles(item, options);

        return options;
    }

    // Tracks inside the container. Jellyfin has already probed these, so there's no cost
    // to listing them and no guessing about what they are.
    private static void AddEmbeddedSubtitles(BaseItem item, List<SubtitleOption> options)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        foreach (var stream in item.GetMediaStreams())
        {
            if (stream.Type != MediaStreamType.Subtitle || !string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            var extension = SubtitleExtractor.GetExtensionForCodec(stream.Codec);
            var format = extension is null
                ? (stream.Codec ?? "unknown").ToLowerInvariant()
                : extension[1..];

            options.Add(new SubtitleOption
            {
                Path = SubtitleExtractor.BuildKey(stream.Index),
                DisplayName = BuildEmbeddedName(stream, format),
                Language = stream.Language,
                Format = format,
                IsEmbedded = true,
                Codec = stream.Codec,

                // Picture based tracks are listed so it's clear why they aren't offered,
                // rather than leaving someone wondering where their eleven subtitles went.
                Supported = extension is not null
            });
        }
    }

    private static string BuildEmbeddedName(MediaStream stream, string format)
    {
        var label = stream.DisplayTitle;

        if (string.IsNullOrWhiteSpace(label))
        {
            label = string.IsNullOrWhiteSpace(stream.Language) ? "Track " + stream.Index.ToString(CultureInfo.InvariantCulture) : stream.Language;
        }

        return $"{label} [in video, {format}]";
    }

    // Jellyfin's own database can lag behind what's actually on disk - a subtitle file
    // dropped into a folder doesn't show up as a MediaStream until that item gets
    // rescanned, which might not happen for a long time on a big library. Rather than
    // making people manually refresh every item, just look in the folder directly too.
    private void AddSubtitlesFromDisk(BaseItem item, List<SubtitleOption> options, HashSet<string> seenPaths)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        var folder = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var files = ReadFolder(folder);

        // A whole season of episodes shares one folder, so "every subtitle in the folder"
        // would hand episode 1 the subtitles for the entire season. When the folder holds
        // more than one video, only take subtitles named after this item's own file, which
        // is the convention every subtitle tool follows. A folder with a single video in it
        // keeps the looser behaviour, since that's where oddly named subtitles like
        // "English.srt" turn up and they can only belong to the one video.
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var restrictToStem = CountVideoLikeFiles(files, item.Path) > 1;

        Collect(files, stem, restrictToStem, options, seenPaths);

        // Then the Subs/Subtitles folders, both the shared one beside the video and a
        // per-video one named after it, which is how a few rippers lay things out. Inside
        // a folder named after this video the loose naming is fine again - everything in
        // there belongs to it.
        foreach (var subfolder in SubtitleSubfolders)
        {
            Collect(ReadFolder(Path.Combine(folder, subfolder)), stem, restrictToStem, options, seenPaths);
            Collect(ReadFolder(Path.Combine(folder, stem, subfolder)), stem, false, options, seenPaths);
        }

        Collect(ReadFolder(Path.Combine(folder, stem)), stem, false, options, seenPaths);
    }

    private static void Collect(
        string[] files,
        string stem,
        bool restrictToStem,
        List<SubtitleOption> options,
        HashSet<string> seenPaths)
    {
        foreach (var file in files)
        {
            if (!SubtitleFormats.IsSubtitle(file) || IsWorkFile(file))
            {
                continue;
            }

            if (restrictToStem && !MatchesStem(file, stem))
            {
                continue;
            }

            if (!seenPaths.Add(file))
            {
                continue;
            }

            options.Add(Describe(file, Path.GetFileName(file), null));
        }
    }

    // The runner writes its scratch files next to the subtitle it's working on, and they
    // are real .srt files while a sync is in flight. They aren't anybody's subtitle, so
    // they have no business showing up in a picker.
    private static bool IsWorkFile(string path)
    {
        return Path.GetFileName(path).Contains(".lapse-", StringComparison.OrdinalIgnoreCase);
    }

    // Subtitle files are named after the video with the language tacked on, but not
    // always with the same punctuation the video uses - "Show.S01E01.en.srt" next to
    // "Show S01E01.mkv" is common enough that a plain StartsWith was throwing away
    // subtitles that obviously belonged to the item. Compare with the separators taken
    // out so those all land on each other.
    private static bool MatchesStem(string file, string stem)
    {
        var name = Path.GetFileNameWithoutExtension(file);

        return name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            || Simplify(name).StartsWith(Simplify(stem), StringComparison.Ordinal);
    }

    private static string Simplify(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static SubtitleOption Describe(string path, string displayName, string? language)
    {
        var kind = SubtitleFormats.GetKind(path);

        return new SubtitleOption
        {
            Path = path,
            DisplayName = displayName,
            Language = language,
            Format = SubtitleFormats.GetName(path),
            NeedsConversion = kind == SubtitleFormatKind.Convertible,
            Supported = kind != SubtitleFormatKind.ImageBased
        };
    }

    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "item.Path is Jellyfin's own resolved library path for an item already looked up by GetItemById, not a raw path from the request.")]
    private string[] ReadFolder(string folder)
    {
        var now = DateTime.UtcNow;

        if (_folderCache.TryGetValue(folder, out var cached) && now - cached.ReadUtc < FolderCacheFor)
        {
            return cached.Files;
        }

        string[] files;
        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
        }

        _folderCache[folder] = (files, now);
        Prune(now);
        return files;
    }

    // The cache is meant to live for one dashboard page load, but nothing ever came back
    // to clear it out, so on a big library it would sit there holding a file listing for
    // every folder in it. Anything past its 15 seconds is dead weight - drop it once
    // there's enough in here to be worth walking.
    private void Prune(DateTime now)
    {
        if (_folderCache.Count < 256)
        {
            return;
        }

        foreach (var entry in _folderCache)
        {
            if (now - entry.Value.ReadUtc >= FolderCacheFor)
            {
                _folderCache.TryRemove(entry.Key, out _);
            }
        }
    }

    // Good enough to answer "is this folder holding one video or a whole season". Counts
    // anything that isn't a subtitle, an image or a metadata file as a video.
    private static int CountVideoLikeFiles(IReadOnlyList<string> files, string itemPath)
    {
        var itemExtension = Path.GetExtension(itemPath);

        return files.Count(f => string.Equals(Path.GetExtension(f), itemExtension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether a path looks like a subtitle file we can work with.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the extension is one of the subtitle formats we handle.</returns>
    public static bool IsSubtitleFile(string path)
    {
        return SubtitleFormats.IsNative(path);
    }
}
