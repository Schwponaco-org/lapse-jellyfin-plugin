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
/// What a shift did.
/// </summary>
public class ShiftResult
{
    /// <summary>
    /// Gets or sets how many timestamps were moved.
    /// </summary>
    public int Shifted { get; set; }

    /// <summary>
    /// Gets or sets where the shifted subtitle went (depends on the output mode, like a sync).
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
/// Moves every timestamp in a subtitle forward or back, for hand tuning when a sync is
/// close but not quite. No engine involved, just text editing.
/// </summary>
public partial class SubtitleShifter
{
    // 00:01:23,456 (srt), 00:01:23.456 / 01:23.456 (vtt, hours optional), 0:01:23.45 (ass).
    // Widths are captured so each one gets written back in its own shape, ass with ms
    // timings isn't valid ass anymore.
    [GeneratedRegex(@"(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<f>\d{1,3})")]
    private static partial Regex TimestampRegex();

    // vtt karaoke timestamps inside the text, <00:01:02.500>. Absolute, so they move too.
    [GeneratedRegex(@"<(?<t>(?:\d{1,3}:)?\d{2}:\d{2}\.\d{3})>")]
    private static partial Regex InlineTimestampRegex();

    // Anything in {curly braces} on an ass line is a style override, not dialogue.
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTagRegex();

    // The two timing fields on an ass/ssa event line: "Dialogue: 0,0:00:01.00,0:00:03.50,..."
    private const int AssTimestampsPerLine = 2;

    // Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text (text can have commas)
    private const int AssEventFieldCount = 10;

    /// <summary>
    /// Checks whether a file is one this can work on at all.
    /// </summary>
    public static bool IsShiftable(string subtitlePath)
    {
        return SubtitleFormats.IsNative(subtitlePath);
    }

    /// <summary>
    /// Gets whether a path is ass/ssa (timings on Dialogue lines instead of arrow lines).
    /// </summary>
    public static bool IsAss(string subtitlePath)
    {
        var extension = Path.GetExtension(subtitlePath);
        return string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the first cue so the shift dialog can show a real line moving.
    /// </summary>
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

    // ass timings are fields 2 and 3 of 10, dialogue last. Shown as "start --> end" like
    // the other formats since that's what the dialog expects.
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

    // drop {\...} overrides and turn \N into a space so it reads like what's on screen
    private static string StripAssTags(string text)
    {
        var withoutBreaks = text.Replace("\\N", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("\\h", " ", StringComparison.Ordinal);

        return AssTagRegex().Replace(withoutBreaks, string.Empty);
    }

    /// <summary>
    /// Applies an offset to a timing line without touching any file, for the preview.
    /// </summary>
    public static string PreviewShift(string timingLine, int offsetMs)
    {
        return ShiftTimingLine(timingLine, TimeSpan.FromMilliseconds(offsetMs), int.MaxValue, out _);
    }

    /// <summary>
    /// Shifts every timestamp (positive = later). The result follows the output mode just
    /// like a sync, a hand shift is just as destructive so it gets the same backups.
    /// </summary>
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

        // held from read to write so two shifts stack instead of racing
        using var fileLock = await SubtitleFileLock.AcquireAsync(subtitlePath, cancellationToken).ConfigureAwait(false);

        var offset = TimeSpan.FromSeconds(offsetSeconds);

        // keep the original encoding and line endings, only the numbers should change
        var document = await SubtitleEncoding.ReadDocumentAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>(document.Lines);
        var isAss = IsAss(subtitlePath);
        var isVtt = string.Equals(Path.GetExtension(subtitlePath), ".vtt", StringComparison.OrdinalIgnoreCase);
        var shiftedCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            // only timing lines, dialogue can contain things that look like timestamps
            var line = lines[i];

            if (isAss)
            {
                // dialogue is on the same line, so only take the first two matches (Start, End)
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

    // Every timed ass event line. Comments aren't rendered but should still move with everything else.
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

    // Moves start and end together. A cue pushed before zero keeps its length and starts
    // at 0 (like the engine and alass do), clamping each end alone used to shorten or kill it.
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
        // can't start before the movie does
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

    // Writes a timestamp back in the shape it came in. Players refuse files that mix ass
    // and srt style times. Hourless vtt stays hourless until it passes the hour.
    private static string FormatLike(Match match, TimeSpan value)
    {
        var fractionDigits = match.Groups["f"].Value.Length;
        var separator = match.Groups["sep"].Success ? match.Groups["sep"].Value : ".";
        var hourDigits = match.Groups["h"].Success ? match.Groups["h"].Value.Length : 0;

        return Format(value, separator, hourDigits, fractionDigits);
    }

    /// <summary>
    /// Formats a subtitle time, rounded (not truncated) to the format's precision.
    /// Truncating made ass lines up to 9ms early, the engine fixed the same thing in 2.2.4.
    /// </summary>
    internal static string Format(TimeSpan value, string separator, int hourDigits, int fractionDigits)
    {
        var unit = fractionDigits switch
        {
            1 => 100L,
            2 => 10L,
            _ => 1L
        };

        // round the whole thing so 59.996s carries into the next minute
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

        // hourless vtt that grew an hour gets two digits, everything else keeps its width
        var width = hourDigits == 0 ? 2 : hourDigits;
        return hours.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + ":" + clock;
    }
}
