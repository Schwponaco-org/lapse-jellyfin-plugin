// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// What a shift did: how many timestamps moved, where the result landed, and what got
/// backed up on the way.
/// </summary>
public class ShiftResult
{
    /// <summary>
    /// Gets or sets how many timestamps were moved.
    /// </summary>
    public int Shifted { get; set; }

    /// <summary>
    /// Gets or sets the file the shifted subtitle was written to. Which file that is
    /// depends on the configured output mode, the same as a sync.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backup that was taken first, if the output mode asked for one.
    /// </summary>
    public string? BackupPath { get; set; }
}

/// <summary>
/// One cue's timings, for showing what a shift would do before committing to it.
/// </summary>
public class SubtitlePreview
{
    /// <summary>
    /// Gets or sets the timing line exactly as it appears in the file.
    /// </summary>
    public string TimingLine { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dialogue that goes with it, trimmed to something short.
    /// </summary>
    public string? Text { get; set; }
}

/// <summary>
/// Nudges every timestamp in a subtitle file forward or backward. This is for when a sync
/// gets you close but the result is still slightly off and you just want to hand-tune it.
/// Doesn't involve the engine at all, it's only text editing.
/// </summary>
public partial class SubtitleShifter
{
    // Matches timestamps like 00:01:23,456 (srt) and 00:01:23.456 (vtt).
    [GeneratedRegex(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<ms>\d{1,3})")]
    private static partial Regex TimestampRegex();

    /// <summary>
    /// Checks whether a file is one this can work on at all.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>True for the formats with plain text timestamps.</returns>
    public static bool IsShiftable(string subtitlePath)
    {
        var extension = Path.GetExtension(subtitlePath);
        return string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".vtt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the first real cue out of a subtitle file, so the dialog can show what a
    /// given offset would do to a line the user recognises rather than to an abstraction.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first cue, or null if the file has none.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path is one the library reported for an item, checked by the controller before it gets here.")]
    public static async Task<SubtitlePreview?> ReadFirstCueAsync(string subtitlePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(subtitlePath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(subtitlePath, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var text = i + 1 < lines.Length ? lines[i + 1].Trim() : null;

            return new SubtitlePreview
            {
                TimingLine = lines[i].Trim(),
                Text = string.IsNullOrWhiteSpace(text) ? null : Shorten(text)
            };
        }

        return null;
    }

    /// <summary>
    /// Applies an offset to a timing line without touching any file, for the preview.
    /// </summary>
    /// <param name="timingLine">A line containing one or two timestamps.</param>
    /// <param name="offsetMs">How far to move it, in milliseconds.</param>
    /// <returns>The line with its timestamps moved.</returns>
    public static string PreviewShift(string timingLine, int offsetMs)
    {
        var offset = TimeSpan.FromMilliseconds(offsetMs);
        return TimestampRegex().Replace(timingLine, match => ShiftOne(match, offset));
    }

    /// <summary>
    /// Shifts all the timestamps in a subtitle file. Positive moves subtitles later,
    /// negative moves them earlier.
    ///
    /// Where the result lands follows the configured output mode, exactly like a sync
    /// does - shifting a subtitle by hand is no less destructive than syncing it, so it
    /// gets the same backup and sidecar promises rather than always editing in place.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file to read.</param>
    /// <param name="offsetSeconds">How far to move it, in seconds. Can be negative.</param>
    /// <param name="outputMode">Where to put the result, or null for the configured default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the shift did.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The input path is one the library reported for an item and the output is derived from it with a fixed suffix.")]
    public async Task<ShiftResult> ShiftAsync(
        string subtitlePath,
        double offsetSeconds,
        OutputMode? outputMode = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsShiftable(subtitlePath))
        {
            throw new NotSupportedException(
                $"Shifting only works on .srt and .vtt files, not {Path.GetExtension(subtitlePath)}");
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

        var mode = EngineRunner.ResolveOutputMode(outputMode);
        var destination = EngineRunner.ResolveDestination(subtitlePath, mode);
        var backup = EngineRunner.TakeBackup(destination, mode);

        await File.WriteAllLinesAsync(destination, lines, cancellationToken).ConfigureAwait(false);

        return new ShiftResult
        {
            Shifted = shiftedCount,
            OutputPath = destination,
            BackupPath = backup
        };
    }

    private static string Shorten(string text)
    {
        return text.Length > 60 ? text[..60] + "..." : text;
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
