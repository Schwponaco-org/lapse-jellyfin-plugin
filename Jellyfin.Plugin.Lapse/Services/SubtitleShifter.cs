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
    // Matches timestamps like 00:01:23,456 (srt), 00:01:23.456 and 01:23.456 (vtt, which
    // leaves the hours off when there are none) and 0:01:23.45 (ass/ssa, which counts in
    // centiseconds and writes a single digit hour). The widths are captured rather than
    // assumed so each format can be written back the way it came in - an ass file with
    // millisecond timings in it is not an ass file any more.
    [GeneratedRegex(@"(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<f>\d{1,3})")]
    private static partial Regex TimestampRegex();

    // A vtt cue can carry timestamps inside its text, <00:01:02.500>, to reveal a line a
    // word at a time. They're absolute, so they move with the cue.
    [GeneratedRegex(@"<(?<t>(?:\d{1,3}:)?\d{2}:\d{2}\.\d{3})>")]
    private static partial Regex InlineTimestampRegex();

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
        return ShiftTimingLine(timingLine, TimeSpan.FromMilliseconds(offsetMs), int.MaxValue, out _);
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

        // Held from the read to the write, so a second shift of the same file lands on
        // top of this one instead of on the same starting point, or on a half written file.
        using var fileLock = await SubtitleFileLock.AcquireAsync(subtitlePath, cancellationToken).ConfigureAwait(false);

        var offset = TimeSpan.FromSeconds(offsetSeconds);

        // Read with its encoding and line endings, so what goes back out is the same file
        // with different numbers in it rather than the same subtitle re-encoded as UTF-8.
        var document = await SubtitleEncoding.ReadDocumentAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>(document.Lines);
        var isAss = IsAss(subtitlePath);
        var isVtt = string.Equals(Path.GetExtension(subtitlePath), ".vtt", StringComparison.OrdinalIgnoreCase);
        var shiftedCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            // Only touch the timing lines. Subtitle text could contain something that
            // looks like a timestamp and we'd rather not mangle someone's dialogue.
            var line = lines[i];

            if (isAss)
            {
                // ass and ssa put the timings on their event lines, and the dialogue is on
                // the same line right after them. Only the first two matches are taken,
                // which keeps this to the Start and End fields and off anything in the text.
                if (IsAssEventLine(line))
                {
                    lines[i] = ShiftTimingLine(line, offset, AssTimestampsPerLine, out var moved);
                    shiftedCount += moved;
                }

                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                lines[i] = ShiftTimingLine(line, offset, int.MaxValue, out var moved);
                shiftedCount += moved;
                continue;
            }

            if (isVtt && line.Contains('<', StringComparison.Ordinal))
            {
                lines[i] = InlineTimestampRegex().Replace(line, match =>
                {
                    var inner = TimestampRegex().Match(match.Groups["t"].Value);
                    return inner.Success ? "<" + FormatLike(inner, Clamp(Parse(inner) + offset)) + ">" : match.Value;
                });
            }
        }

        var mode = EngineRunner.ResolveOutputMode(outputMode);
        var destination = EngineRunner.ResolveDestination(subtitlePath, mode);
        var backup = EngineRunner.TakeBackup(destination, mode);

        await SubtitleFileLock.WriteDocumentAsync(destination, document, lines, cancellationToken).ConfigureAwait(false);

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

    // Moves the start and end of one cue together. A cue moved to before the start of the
    // film keeps its length and starts at zero, the way the engine and alass both handle
    // it; pinning each end on its own used to cut a cue short, or to nothing at all when
    // both ends went under.
    private static string ShiftTimingLine(string line, TimeSpan offset, int count, out int moved)
    {
        var matches = TimestampRegex().Matches(line);
        var taken = Math.Min(matches.Count, count);
        moved = taken;

        if (taken == 0)
        {
            return line;
        }

        var times = new TimeSpan[taken];
        for (var i = 0; i < taken; i++)
        {
            times[i] = Parse(matches[i]) + offset;
        }

        if (taken >= 2 && times[0] < TimeSpan.Zero)
        {
            var length = times[1] - times[0];
            times[0] = TimeSpan.Zero;
            times[1] = length > TimeSpan.Zero ? length : TimeSpan.Zero;
        }

        var builder = new System.Text.StringBuilder(line.Length + 8);
        var at = 0;

        for (var i = 0; i < taken; i++)
        {
            builder.Append(line, at, matches[i].Index - at);
            builder.Append(FormatLike(matches[i], Clamp(times[i])));
            at = matches[i].Index + matches[i].Length;
        }

        builder.Append(line, at, line.Length - at);
        return builder.ToString();
    }

    private static TimeSpan Clamp(TimeSpan value)
    {
        // Subtitles can't start before the movie does.
        return value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    private static TimeSpan Parse(Match match)
    {
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;

        return new TimeSpan(
            0,
            hours,
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["f"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));
    }

    // Writes a timestamp back in the shape it was read in. srt and vtt want two digit
    // hours and milliseconds; ass and ssa want a single digit hour and centiseconds, and
    // a player will refuse a file that mixes them up. vtt without hours stays without
    // them until a shift takes it past the hour, where it needs them.
    private static string FormatLike(Match match, TimeSpan value)
    {
        var fractionDigits = match.Groups["f"].Value.Length;
        var separator = match.Groups["sep"].Success ? match.Groups["sep"].Value : ".";
        var hourDigits = match.Groups["h"].Success ? match.Groups["h"].Value.Length : 0;

        return Format(value, separator, hourDigits, fractionDigits);
    }

    /// <summary>
    /// Formats a time the way a subtitle writes it, rounded to the precision the format
    /// keeps rather than cut off at it. Cutting off put every ass line up to 9ms early, and
    /// the engine stopped doing that in 2.2.4.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <param name="separator">The character between seconds and the fraction.</param>
    /// <param name="hourDigits">How many digits the hour gets, or 0 to leave it off while
    /// the time is under an hour.</param>
    /// <param name="fractionDigits">1, 2 or 3: tenths, hundredths or thousandths.</param>
    /// <returns>The formatted time.</returns>
    internal static string Format(TimeSpan value, string separator, int hourDigits, int fractionDigits)
    {
        var unit = fractionDigits switch
        {
            1 => 100L,
            2 => 10L,
            _ => 1L
        };

        // Rounded as a whole, so 59.996 seconds carries into the next minute rather than
        // coming out as a 60th second.
        var units = (long)Math.Round(value.TotalMilliseconds / unit, MidpointRounding.AwayFromZero);
        var rounded = TimeSpan.FromMilliseconds(units * unit);
        var fraction = rounded.Milliseconds / unit;

        var hours = (int)rounded.TotalHours;
        var fractionText = fraction.ToString(CultureInfo.InvariantCulture).PadLeft(fractionDigits, '0');
        var clock = string.Create(CultureInfo.InvariantCulture, $"{rounded.Minutes:00}:{rounded.Seconds:00}{separator}{fractionText}");

        if (hourDigits == 0 && hours == 0)
        {
            return clock;
        }

        // A vtt time that grew an hour gets the two digit hour vtt uses; everything else
        // keeps the width it came in with.
        var width = hourDigits == 0 ? 2 : hourDigits;
        return hours.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + ":" + clock;
    }
}
