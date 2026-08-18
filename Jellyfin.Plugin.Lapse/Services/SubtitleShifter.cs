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
    // Matches timestamps like 00:01:23,456 (srt), 00:01:23.456 (vtt) and 0:01:23.45
    // (ass/ssa, which counts in centiseconds and writes a single digit hour). The widths
    // are captured rather than assumed so each format can be written back the way it
    // came in - an ass file with millisecond timings in it is not an ass file any more.
    [GeneratedRegex(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<f>\d{1,3})")]
    private static partial Regex TimestampRegex();

    // Anything in {curly braces} on an ass line is a style override, not dialogue.
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTagRegex();

    // The two timing fields on an ass/ssa event line: "Dialogue: 0,0:00:01.00,0:00:03.50,..."
    private const int AssTimestampsPerLine = 2;

    // Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text. The text
    // is last and may itself contain commas, which is why it's a limited split.
    private const int AssEventFieldCount = 10;

    /// <summary>
    /// Checks whether a file is one this can work on at all.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>True for the formats with plain text timestamps: srt, vtt, ass and ssa.</returns>
    public static bool IsShiftable(string subtitlePath)
    {
        return SubtitleFormats.IsNative(subtitlePath);
    }

    /// <summary>
    /// Gets whether a path is an ass/ssa file, which keeps its timings on Dialogue lines
    /// rather than on a line of its own with an arrow in it.
    /// </summary>
    /// <param name="subtitlePath">The subtitle file.</param>
    /// <returns>True for .ass and .ssa.</returns>
    public static bool IsAss(string subtitlePath)
    {
        var extension = Path.GetExtension(subtitlePath);
        return string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase);
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

        var lines = await SubtitleEncoding.ReadAllLinesAsync(subtitlePath, cancellationToken).ConfigureAwait(false);

        return IsAss(subtitlePath) ? ReadFirstAssCue(lines) : ReadFirstTextCue(lines);
    }

    private static SubtitlePreview? ReadFirstTextCue(string[] lines)
    {
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

    // An ass event line carries its timings as the second and third of ten comma
    // separated fields, with the dialogue as the last one. The preview shows them in the
    // same "start --> end" shape the other formats use, since that's what the dialog is
    // built to display and the point is to see the numbers move.
    private static SubtitlePreview? ReadFirstAssCue(string[] lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line.Split(',', AssEventFieldCount);
            if (fields.Length < AssEventFieldCount)
            {
                continue;
            }

            var text = StripAssTags(fields[^1]).Trim();

            return new SubtitlePreview
            {
                TimingLine = fields[1].Trim() + " --> " + fields[2].Trim(),
                Text = string.IsNullOrWhiteSpace(text) ? null : Shorten(text)
            };
        }

        return null;
    }

    // Drops the {\pos(...)} style override blocks and turns the hard line break marker
    // into a space, so the example line reads as the words on screen.
    private static string StripAssTags(string text)
    {
        var withoutBreaks = text.Replace("\\N", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("\\h", " ", StringComparison.Ordinal);

        return AssTagRegex().Replace(withoutBreaks, string.Empty);
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
                $"Shifting works on .srt, .vtt, .ass and .ssa files, not {Path.GetExtension(subtitlePath)}. Convert it first.");
        }

        if (!File.Exists(subtitlePath))
        {
            throw new FileNotFoundException("Subtitle file not found", subtitlePath);
        }

        var offset = TimeSpan.FromSeconds(offsetSeconds);
        var lines = await SubtitleEncoding.ReadAllLinesAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        var isAss = IsAss(subtitlePath);
        var shiftedCount = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            // Only touch the timing lines. Subtitle text could contain something that
            // looks like a timestamp and we'd rather not mangle someone's dialogue.
            var line = lines[i];

            if (isAss)
            {
                // ass and ssa put the timings on their event lines, and the dialogue is on
                // the same line right after them. Replacing only the first two matches
                // keeps this to the Start and End fields and off anything in the text.
                if (!IsAssEventLine(line))
                {
                    continue;
                }

                lines[i] = TimestampRegex().Replace(
                    line,
                    match =>
                    {
                        shiftedCount++;
                        return ShiftOne(match, offset);
                    },
                    AssTimestampsPerLine);

                continue;
            }

            if (!line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            lines[i] = TimestampRegex().Replace(line, match =>
            {
                shiftedCount++;
                return ShiftOne(match, offset);
            });
        }

        var mode = EngineRunner.ResolveOutputMode(outputMode);
        var destination = EngineRunner.ResolveDestination(subtitlePath, mode);
        var backup = EngineRunner.TakeBackup(destination, mode);

        await File.WriteAllLinesAsync(destination, lines, SubtitleEncoding.Utf8NoBom, cancellationToken).ConfigureAwait(false);

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

    // Says whether an ass/ssa line is one of the ones with timings on it. Comment lines
    // are timed the same way as Dialogue ones and a renderer ignores them, but leaving
    // them where they were would put them out of step with everything around them.
    private static bool IsAssEventLine(string line)
    {
        var trimmed = line.TrimStart();

        return trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Picture:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Sound:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Movie:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Command:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShiftOne(Match match, TimeSpan offset)
    {
        var fraction = match.Groups["f"].Value;

        var original = new TimeSpan(
            0,
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture));

        var shifted = original + offset;

        // Subtitles can't start before the movie does, so anything that would go negative
        // just gets pinned to zero.
        if (shifted < TimeSpan.Zero)
        {
            shifted = TimeSpan.Zero;
        }

        return Format(
            shifted,
            match.Groups["sep"].Value,
            match.Groups["h"].Value.Length,
            fraction.Length);
    }

    // Writes a timestamp back in the shape it was read in. srt and vtt want two digit
    // hours and milliseconds; ass and ssa want a single digit hour and centiseconds, and
    // a player will refuse a file that mixes them up.
    private static string Format(TimeSpan value, string separator, int hourDigits, int fractionDigits)
    {
        var hours = (int)value.TotalHours;
        var fraction = fractionDigits switch
        {
            1 => value.Milliseconds / 100,
            2 => value.Milliseconds / 10,
            _ => value.Milliseconds
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours.ToString(CultureInfo.InvariantCulture).PadLeft(hourDigits, '0')}:{value.Minutes:00}:{value.Seconds:00}{separator}{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(fractionDigits, '0')}");
    }
}
