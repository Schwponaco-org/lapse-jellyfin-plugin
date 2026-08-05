// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Finds the external subtitle files attached to a movie. The LAPSE engine works on
/// external subtitle files (srt/ass/etc sitting next to the video), not embedded ones.
/// </summary>
public class SubtitleLocator
{
    private static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>
    /// Gets every external subtitle file for the given movie.
    /// </summary>
    /// <param name="item">The movie.</param>
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
    // dropped into a movie's folder doesn't show up as a MediaStream until that item gets
    // rescanned, which might not happen for a long time on a big library. Rather than
    // making people manually refresh every movie, just look in the folder directly too.
    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "item.Path is Jellyfin's own resolved library path for an item already looked up by GetItemById, not a raw path from the request.")]
    private static void AddSubtitlesFromDisk(BaseItem item, List<SubtitleOption> options, HashSet<string> seenPaths)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        var folder = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!seenPaths.Add(file) || !IsSubtitleFile(file))
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
