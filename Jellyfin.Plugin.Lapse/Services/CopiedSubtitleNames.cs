// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Names a subtitle moved from one copy of a film onto another. It takes the target video's
/// name and keeps what came after the source video's name (language, forced, sdh), so
/// "Film - TV.en.forced.srt" moved onto "Film - Bluray.mkv" becomes "Film - Bluray.en.forced.srt"
/// and Jellyfin files it under the right version.
/// </summary>
public static partial class CopiedSubtitleNames
{
    /// <summary>
    /// Gets where the moved subtitle goes. Never over a subtitle that's already there, that one
    /// may well be right, so a taken name gets the sidecar suffix instead.
    /// </summary>
    public static string ResolveDestination(string targetVideo, string sourceVideo, string sourceSubtitle, string? language, string? outputFormat)
    {
        var extension = string.IsNullOrEmpty(outputFormat) ? Path.GetExtension(sourceSubtitle) : "." + outputFormat;
        var folder = Path.GetDirectoryName(targetVideo) ?? string.Empty;
        var wanted = Path.Combine(folder, Path.GetFileNameWithoutExtension(targetVideo) + Tail(sourceVideo, sourceSubtitle, language) + extension);

        return File.Exists(wanted) ? EngineRunner.ResolveDestination(wanted, OutputMode.SidecarOnly) : wanted;
    }

    /// <summary>
    /// Gets the part of the name after the video's own, ".en.forced" and so on. A subtitle
    /// that isn't named after its video gets its language, when it has one.
    /// </summary>
    public static string Tail(string sourceVideo, string sourceSubtitle, string? language)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceVideo);
        var name = Path.GetFileNameWithoutExtension(sourceSubtitle);
        var suffix = Plugin.Instance?.Configuration.ResolveSidecarSuffix() ?? ".shifted";

        // a synced sidecar of the source is still the same subtitle
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
        {
            name = name[..^suffix.Length];
        }

        var tokens = new List<string>();
        if (name.StartsWith(stem + ".", StringComparison.Ordinal))
        {
            // the stream number of an extracted track counts the source video's tracks, not ours
            tokens.AddRange(name[(stem.Length + 1)..].Split('.').Where(t => t.Length > 0 && !StreamMarker().IsMatch(t)));
        }

        if (tokens.Count == 0 && !string.IsNullOrWhiteSpace(language)
            && ExtractedSubtitleNames.LanguageTag(language) is var tag && tag != "und")
        {
            tokens.Add(tag);
        }

        return tokens.Count == 0 ? string.Empty : "." + string.Join('.', tokens);
    }

    // ".stream2" from our extraction, ".track2" from older versions
    [GeneratedRegex("^(stream|track)[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamMarker();
}
