// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
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
    /// <summary>
    /// Gets every external subtitle file for the given movie.
    /// </summary>
    /// <param name="item">The movie.</param>
    /// <returns>List of subtitle options, empty if there aren't any external subs.</returns>
    public List<SubtitleOption> GetExternalSubtitles(BaseItem item)
    {
        var options = new List<SubtitleOption>();

        foreach (var stream in item.GetMediaStreams())
        {
            if (stream.Type != MediaStreamType.Subtitle || !stream.IsExternal || string.IsNullOrEmpty(stream.Path))
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

        return options;
    }
}
