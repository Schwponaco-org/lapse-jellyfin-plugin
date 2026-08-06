// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Finds the external subtitle files attached to an item. The engines work on external
/// subtitle files (srt/ass/etc sitting next to the video), not embedded ones.
/// </summary>
public class SubtitleLocator
{
    private static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".vtt" };
    private static readonly TimeSpan FolderCacheFor = TimeSpan.FromSeconds(15);

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
            if (stream.Type != MediaStreamType.Subtitle || !stream.IsExternal || string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            if (!seenPaths.Add(stream.Path))
            {
                continue;
            }

            var fileName = Path.GetFileName(stream.Path);
            var displayName = string.IsNullOrEmpty(stream.Language) ? fileName : $"{stream.Language} ({fileName})";

            options.Add(new SubtitleOption
            {
                Path = stream.Path,
                DisplayName = displayName,
                Language = stream.Language
            });
        }

        AddSubtitlesFromDisk(item, options, seenPaths);

        return options;
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
        if (files.Length == 0)
        {
            return;
        }

        // A whole season of episodes shares one folder, so "every subtitle in the folder"
        // would hand episode 1 the subtitles for the entire season. When the folder holds
        // more than one video, only take subtitles named after this item's own file, which
        // is the convention every subtitle tool follows. A folder with a single video in it
        // keeps the looser behaviour, since that's where oddly named subtitles like
        // "English.srt" turn up and they can only belong to the one video.
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var restrictToStem = CountVideoLikeFiles(files, item.Path) > 1;

        foreach (var file in files)
        {
            if (!IsSubtitleFile(file))
            {
                continue;
            }

            if (restrictToStem && !Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seenPaths.Add(file))
            {
                continue;
            }

            options.Add(new SubtitleOption
            {
                Path = file,
                DisplayName = Path.GetFileName(file)
            });
        }
    }

    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "item.Path is Jellyfin's own resolved library path for an item already looked up by GetItemById, not a raw path from the request.")]
    private string[] ReadFolder(string folder)
    {
        if (_folderCache.TryGetValue(folder, out var cached) && DateTime.UtcNow - cached.ReadUtc < FolderCacheFor)
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

        _folderCache[folder] = (files, DateTime.UtcNow);
        return files;
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
        var extension = Path.GetExtension(path);
        foreach (var subtitleExtension in SubtitleExtensions)
        {
            if (string.Equals(extension, subtitleExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
