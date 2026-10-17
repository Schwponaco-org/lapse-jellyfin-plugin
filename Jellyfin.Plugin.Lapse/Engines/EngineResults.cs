// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Small helpers the engine implementations share for building results.
/// </summary>
public static partial class EngineResults
{
    // ffmpeg decoder chatter ("[mp3 @ 0x7ff9...] Header missing" once per frame, hundreds
    // of lines on one film). Not the reason a run failed, so it never reaches the user.
    [GeneratedRegex(@"^\[[a-z0-9_,]+ @ 0x[0-9a-f]+\]", RegexOptions.IgnoreCase)]
    private static partial Regex LibavNoiseRegex();

    // a traceback's last line is the actual exception
    [GeneratedRegex(@"^\s+(at |File ""|\.\.\.)", RegexOptions.IgnoreCase)]
    private static partial Regex StackFrameRegex();

    /// <summary>
    /// Builds a failure result from whatever the engine printed.
    /// </summary>
    public static SyncResult Failure(SyncMode mode, string stderr, int exitCode)
    {
        var error = Summarize(stderr, 400)
            ?? string.Format(CultureInfo.InvariantCulture, "The engine exited with code {0} and said nothing about why.", exitCode);

        return new SyncResult { Success = false, Mode = mode, Error = error };
    }

    /// <summary>
    /// Boils engine output down to something readable: decoder noise dropped, repeats
    /// collapsed, last few lines kept. The full output still goes to the debug log.
    /// </summary>
    public static string? Summarize(string text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var meaningful = new List<string>();
        var noiseCount = 0;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimEnd('\r');

            if (line.Length == 0 || StackFrameRegex().IsMatch(raw))
            {
                continue;
            }

            if (LibavNoiseRegex().IsMatch(line))
            {
                noiseCount++;
                continue;
            }

            // same line fifty times is one line
            if (meaningful.Count == 0 || !string.Equals(meaningful[^1], line, StringComparison.Ordinal))
            {
                meaningful.Add(line);
            }
        }

        // only decoder noise, which is the diagnosis: ffmpeg can't read this audio cleanly
        if (meaningful.Count == 0)
        {
            return noiseCount == 0
                ? null
                : "The engine could not decode this file's audio - it produced "
                    + noiseCount.ToString(CultureInfo.InvariantCulture)
                    + " decoder errors and no result. The file's audio track is probably damaged or in an unusual format. The full output is in the server log.";
        }

        // the actual error is usually at the end
        var kept = string.Join(" ", meaningful.TakeLast(3));

        if (noiseCount > 0)
        {
            kept += string.Format(
                CultureInfo.InvariantCulture,
                " (plus {0} decoder warnings, see the server log)",
                noiseCount);
        }

        return kept.Length > maxLength ? kept[..maxLength].TrimEnd() + "..." : kept;
    }
}
