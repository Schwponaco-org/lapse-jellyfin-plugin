// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Finds every subtitle an item has: files next to the video and tracks inside it.
/// Embedded tracks get listed too (on lots of libraries that's all there is) and are
/// extracted to a file the first time something needs one, see <see cref="SubtitleExtractor"/>.
/// </summary>
public class SubtitleLocator
{
    private static readonly TimeSpan FolderCacheFor = TimeSpan.FromSeconds(15);

    // where people and tools put subtitles when not right next to the video
    private static readonly string[] SubtitleSubfolders = { "Subs", "Subtitles" };

    // The status list asks about every item and a TV season shares one folder, so without
    // this the same folder gets listed once per episode. Keyed on the folder's write time
    // too, so a file we just wrote shows up on the next request instead of 15s later.
    private readonly ConcurrentDictionary<string, (FolderListing Listing, DateTime ReadUtc, DateTime FolderWriteUtc)> _folderCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets every subtitle for an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="streams">The item's media streams if the caller already has them, saves a DB query.</param>
    /// <returns>The subtitles, external ones first.</returns>
    public List<SubtitleOption> GetExternalSubtitles(BaseItem item, IReadOnlyList<MediaStream>? streams = null)
    {
        var options = new List<SubtitleOption>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        streams ??= item.GetMediaStreams();

        foreach (var stream in streams)
        {
            // A subtitle stream with a path is external, whatever IsExternal says. That flag
            // is often false for stuff picked up from a Subs folder or written by another
            // plugin. Embedded streams never have a path so they can't get mixed up.
            // (jellyfin's record also outlives the file until the next scan, hence File.Exists)
            if (stream.Type != MediaStreamType.Subtitle
                || string.IsNullOrEmpty(stream.Path)
                || !SubtitleFormats.IsSubtitle(stream.Path)
                || !File.Exists(stream.Path)
                || !seenPaths.Add(stream.Path))
            {
                continue;
            }

            var fileName = Path.GetFileName(stream.Path);
            var displayName = string.IsNullOrEmpty(stream.Language) ? fileName : $"{stream.Language} ({fileName})";
            options.Add(Describe(stream.Path, displayName, stream.Language));
        }

        AddSubtitlesFromDisk(item, options, seenPaths);
        AddEmbeddedSubtitles(item, streams, options);

        return options;
    }

    // Tracks inside the container. Jellyfin already probed these so listing them is free.
    private static void AddEmbeddedSubtitles(BaseItem item, IReadOnlyList<MediaStream> streams, List<SubtitleOption> options)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        foreach (var stream in streams)
        {
            if (stream.Type != MediaStreamType.Subtitle || !string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            var extension = SubtitleExtractor.GetExtensionForCodec(stream.Codec);
            var format = extension is null ? (stream.Codec ?? "unknown").ToLowerInvariant() : extension[1..];

            var label = stream.DisplayTitle;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = string.IsNullOrWhiteSpace(stream.Language)
                    ? "Track " + stream.Index.ToString(CultureInfo.InvariantCulture)
                    : stream.Language;
            }

            options.Add(new SubtitleOption
            {
                Path = SubtitleExtractor.BuildKey(stream.Index),
                DisplayName = $"{label} [in video, {format}]",
                Language = stream.Language,
                Format = format,
                IsEmbedded = true,
                Codec = stream.Codec,

                // picture tracks are listed anyway so it's clear why they can't be picked
                Supported = extension is not null
            });
        }
    }

    // Jellyfin's DB lags behind the disk (a new .srt isn't a stream until the item is
    // rescanned), so look in the folder ourselves as well.
    private void AddSubtitlesFromDisk(BaseItem item, List<SubtitleOption> options, HashSet<string> seenPaths)
    {
        var folder = string.IsNullOrEmpty(item.Path) ? null : Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var listing = ReadFolder(folder);
        var stem = new Stem(Path.GetFileNameWithoutExtension(item.Path!));

        // A season folder holds lots of videos, so only take subtitles named after this one.
        // A folder with a single video keeps the loose matching, that's where "English.srt" lives.
        var restrictToStem = CountVideoLikeFiles(listing.Files, item.Path!) > 1;

        Collect(listing.Files, stem, restrictToStem, options, seenPaths);

        // Subs/ and Subtitles/ beside the video, plus a folder named after the video (some
        // rippers do that). Inside the per-video folder everything belongs to it.
        foreach (var subfolder in SubfoldersNamed(listing, SubtitleSubfolders))
        {
            Collect(ReadFolder(subfolder).Files, stem, restrictToStem, options, seenPaths);
        }

        foreach (var ownFolder in SubfoldersNamed(listing, stem.Raw))
        {
            var own = ReadFolder(ownFolder);
            Collect(own.Files, stem, false, options, seenPaths);

            foreach (var subfolder in SubfoldersNamed(own, SubtitleSubfolders))
            {
                Collect(ReadFolder(subfolder).Files, stem, false, options, seenPaths);
            }
        }
    }

    private static IEnumerable<string> SubfoldersNamed(FolderListing listing, params string[] names)
    {
        foreach (var directory in listing.Directories)
        {
            var name = Path.GetFileName(directory);
            if (Array.Exists(names, n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase)))
            {
                yield return directory;
            }
        }
    }

    private static void Collect(string[] files, Stem stem, bool restrictToStem, List<SubtitleOption> options, HashSet<string> seenPaths)
    {
        foreach (var file in files)
        {
            // ".lapse-" files are the runner's scratch files mid-sync, not real subtitles
            if (!SubtitleFormats.IsSubtitle(file)
                || Path.GetFileName(file).Contains(".lapse-", StringComparison.OrdinalIgnoreCase)
                || (restrictToStem && !stem.Matches(file))
                || !seenPaths.Add(file))
            {
                continue;
            }

            options.Add(Describe(file, Path.GetFileName(file), null));
        }
    }

    private static SubtitleOption Describe(string path, string displayName, string? language)
    {
        var kind = SubtitleFormats.GetKind(path);

        // Whether it needs converting depends on the default engine (LAPSE reads MicroDVD,
        // SAMI, TTML, PGS, VobSub as is). Static list instead of asking the binary, this runs
        // per subtitle on page load and starting a process each time isn't worth it.
        var engineReads = EngineFormats.DefaultEngineCanRead(path);

        return new SubtitleOption
        {
            Path = path,
            DisplayName = displayName,
            Language = language,
            Format = SubtitleFormats.GetName(path),
            NeedsConversion = kind == SubtitleFormatKind.Convertible && !engineReads,
            TextBased = kind != SubtitleFormatKind.ImageBased,

            // picture subtitles are only useful if the engine can line them up
            Supported = kind != SubtitleFormatKind.ImageBased || engineReads
        };
    }

    private FolderListing ReadFolder(string folder)
    {
        var now = DateTime.UtcNow;
        var folderWriteUtc = ReadFolderWriteTime(folder);

        if (_folderCache.TryGetValue(folder, out var cached)
            && now - cached.ReadUtc < FolderCacheFor
            && cached.FolderWriteUtc == folderWriteUtc)
        {
            return cached.Listing;
        }

        var files = new List<string>();
        var directories = new List<string>();

        try
        {
            // one pass for both, the entry type comes with the name on most file systems
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                (entry is DirectoryInfo ? directories : files).Add(entry.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files.Clear();
            directories.Clear();
        }

        var listing = new FolderListing(files.ToArray(), directories.ToArray());
        _folderCache[folder] = (listing, now, folderWriteUtc);
        Prune(now);
        return listing;
    }

    private static DateTime ReadFolderWriteTime(string folder)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    // drop expired entries once there's enough to bother, or it'd hold a listing for every folder in the library
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

    // Good enough to tell "one video" from "a whole season": a season is ripped one way,
    // so count files with the same extension as the item.
    private static int CountVideoLikeFiles(string[] files, string itemPath)
    {
        var itemExtension = Path.GetExtension(itemPath);
        var count = 0;

        foreach (var file in files)
        {
            if (string.Equals(Path.GetExtension(file), itemExtension, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private sealed record FolderListing(string[] Files, string[] Directories);

    // The video's file name without extension. "Show.S01E01.en.srt" next to "Show S01E01.mkv"
    // is common, so besides a plain prefix match it also compares with punctuation stripped.
    private sealed class Stem
    {
        private readonly string _simple;

        public Stem(string raw)
        {
            Raw = raw;
            _simple = Simplify(raw);
        }

        public string Raw { get; }

        public bool Matches(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);

            return name.StartsWith(Raw, StringComparison.OrdinalIgnoreCase)
                || Simplify(name).StartsWith(_simple, StringComparison.Ordinal);
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
    }
}
