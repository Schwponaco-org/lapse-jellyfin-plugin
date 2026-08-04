// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Nudges every timestamp in a subtitle file forward or backward by a few seconds.
/// This is for when a sync gets you close but the result is still slightly off and you
/// just want to hand-tune it. Doesn't involve the engine at all, it's only text editing.
/// </summary>
public partial class SubtitleShifter
{
    // Matches timestamps like 00:01:23,456 (srt) and 00:01:23.456 (vtt).
    [GeneratedRegex(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<ms>\d{1,3})")]
    private static partial Regex TimestampRegex();

    /// <summary>
    /// Shifts all the timestamps in a subtitle file by the given number of seconds.
    /// Positive moves subtitles later, negative moves them earlier.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file to edit, in place.</param>
    /// <param name="offsetSeconds">How far to move it, in seconds. Can be negative.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many timestamps got changed.</returns>
    public async Task<int> ShiftAsync(string subtitlePath, double offsetSeconds, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(subtitlePath);
        if (!string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".vtt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Nudging only works on .srt and .vtt files, not {extension}");
        }

        if (!File.Exists(subtitlePath))
        {
            throw new FileNotFoundException("Subtitle file not found", subtitlePath);
        }

        var offset = TimeSpan.FromSeconds(offsetSeconds);
        var lines = await File.ReadAllLinesAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        var shiftedCount = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            // Only touch the timing lines. Subtitle text could contain something that
            // looks like a timestamp and we'd rather not mangle someone's dialogue.
            if (!lines[i].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            lines[i] = TimestampRegex().Replace(lines[i], match =>
            {
                shiftedCount++;
                return ShiftOne(match, offset);
            });
        }

        await File.WriteAllLinesAsync(subtitlePath, lines, cancellationToken).ConfigureAwait(false);
        return shiftedCount;
    }

    private static string ShiftOne(Match match, TimeSpan offset)
    {
        var original = new TimeSpan(
            0,
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["ms"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));

        var shifted = original + offset;

        // Subtitles can't start before the movie does, so anything that would go negative
        // just gets pinned to zero.
        if (shifted < TimeSpan.Zero)
        {
            shifted = TimeSpan.Zero;
        }

        var separator = match.Groups["sep"].Value;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}{3}{4:000}",
            (int)shifted.TotalHours,
            shifted.Minutes,
            shifted.Seconds,
            separator,
            shifted.Milliseconds);
    }
}
