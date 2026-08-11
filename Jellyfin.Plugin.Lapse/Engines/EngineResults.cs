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
    // FFmpeg's libraries log through their own callback, and on a file with a damaged or
    // unusual stream they will happily print the same complaint once per frame. A single
    // film produced several hundred lines of "[mp3 @ 0x7ff935c2ea80] Header missing",
    // every one of which went into the item's status message. These lines are decoder
    // chatter, not the reason a run failed, so they never reach the user.
    [GeneratedRegex(@"^\[[a-z0-9_,]+ @ 0x[0-9a-f]+\]", RegexOptions.IgnoreCase)]
    private static partial Regex LibavNoiseRegex();

    // A traceback is worth reducing to its last line, which is the actual exception.
    [GeneratedRegex(@"^\s+(at |File ""|\.\.\.)", RegexOptions.IgnoreCase)]
    private static partial Regex StackFrameRegex();

    /// <summary>
    /// Builds a failure result from whatever the engine printed.
    /// </summary>
    /// <param name="mode">The mode that was requested.</param>
    /// <param name="stderr">Whatever the engine printed to stderr.</param>
    /// <param name="exitCode">Process exit code.</param>
    /// <returns>A failed result.</returns>
    public static SyncResult Failure(SyncMode mode, string stderr, int exitCode)
    {
        var error = Summarize(stderr, 400)
            ?? string.Format(CultureInfo.InvariantCulture, "The engine exited with code {0} and said nothing about why.", exitCode);

        return new SyncResult { Success = false, Mode = mode, Error = error };
    }

    /// <summary>
    /// Turns an engine's output into something worth showing a person: decoder noise
    /// dropped, repeats collapsed, and only the last few meaningful lines kept.
    ///
    /// This is what stands between the dashboard and several hundred identical lines of
    /// libav logging. Nothing is lost by filtering here, since the full output still goes
    /// to the server log for anyone actually debugging a file.
    /// </summary>
    /// <param name="text">The raw output.</param>
    /// <param name="maxLength">How much to keep.</param>
    /// <returns>The trimmed text, or null if there was nothing useful in it.</returns>
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

            // The same complaint repeated is one fact, not fifty.
            if (meaningful.Count == 0 || !string.Equals(meaningful[^1], line, StringComparison.Ordinal))
            {
                meaningful.Add(line);
            }
        }

        // Everything it said was decoder noise, which on its own is the diagnosis: the
        // audio in this file is not something ffmpeg can read cleanly.
        if (meaningful.Count == 0)
        {
            return noiseCount == 0
                ? null
                : "The engine could not decode this file's audio - it produced "
                    + noiseCount.ToString(CultureInfo.InvariantCulture)
                    + " decoder errors and no result. The file's audio track is probably damaged or in an unusual format. The full output is in the server log.";
        }

        // The tail is where engines put the thing that actually went wrong.
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
