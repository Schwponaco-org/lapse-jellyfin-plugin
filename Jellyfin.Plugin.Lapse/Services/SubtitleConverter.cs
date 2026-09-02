// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// One subtitle cue, once it's been read out of whatever format it was written in.
/// </summary>
internal sealed class SubtitleCue
{
    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    // Lines of dialogue, already stripped of whatever markup the source format used to
    // hold them apart.
    public List<string> Lines { get; } = new();
}

/// <summary>
/// Turns a subtitle file into another format.
///
/// The four formats the plugin works in - srt, vtt, ass and ssa - are read and written
/// here directly, because they're all plain text and going through a converter that
/// re-encodes them would be a lot of process starting for some string handling. Anything
/// else text based (MicroDVD, SAMI, SubViewer and the rest) is handed to the ffmpeg
/// Jellyfin already ships, which reads far more of them than is worth reimplementing, and
/// comes back as srt to carry on with.
///
/// Picture based subtitles - PGS, VobSub, DVD - hold no text at all, only bitmaps. There
/// is no conversion of those short of running OCR over every frame, so they're turned
/// away with a reason rather than half handled.
/// </summary>
public partial class SubtitleConverter
{
    // 00:01:23,456 / 00:01:23.456 / 0:01:23.45 - every timestamp shape the text formats use.
    [GeneratedRegex(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[,.](?<f>\d{1,3})")]
    private static partial Regex TimestampRegex();

    // Anything in {curly braces} in an ass line is styling, not words.
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTagRegex();

    // <i>, </font color="#fff">, and the rest of what vtt and srt allow inline.
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTagRegex();

    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(2);

    // The two ways an ass line marks a break within one cue.
    private static readonly string[] AssLineBreaks = { "\\N", "\\n" };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SubtitleConverter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleConverter"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin ships.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleConverter(IMediaEncoder mediaEncoder, ILogger<SubtitleConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Says why a file can't be converted, or null when it can be.
    /// </summary>
    /// <param name="sourcePath">The file to convert.</param>
    /// <returns>A message for the caller, or null.</returns>
    public string? GetConversionProblem(string sourcePath)
    {
        switch (SubtitleFormats.GetKind(sourcePath))
        {
            case SubtitleFormatKind.Native:
                return null;

            case SubtitleFormatKind.Convertible:
                return HasFfmpeg()
                    ? null
                    : $"Converting {SubtitleFormats.GetName(sourcePath)} files needs ffmpeg, and the server hasn't told the plugin where its copy is.";

            case SubtitleFormatKind.ImageBased:
                return $"{SubtitleFormats.GetName(sourcePath).ToUpperInvariant()} subtitles are pictures of text, not text, so there's nothing here to convert or line up. Getting one into a usable shape means running OCR over it with something like Subtitle Edit first.";

            default:
                return $"{Path.GetExtension(sourcePath)} isn't a subtitle format the plugin knows.";
        }
    }

    /// <summary>
    /// Converts a subtitle file into another format.
    /// </summary>
    /// <param name="sourcePath">The file to read.</param>
    /// <param name="destinationPath">The file to write. Its extension decides the format.</param>
    /// <param name="style">How the result should look, when it's being written as ass or
    /// ssa. Null writes the plain default style, which is what an ordinary conversion
    /// wants; the other formats carry no styling and ignore this either way.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many cues came across.</returns>
    /// <exception cref="NotSupportedException">The source isn't a format that can be converted.</exception>
    /// <exception cref="InvalidDataException">The source held no cues.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers hand in a subtitle the library lists for an item, and a destination derived from it with a known extension.")]
    public async Task<int> ConvertAsync(
        string sourcePath,
        string destinationPath,
        SubtitleStyle? style = null,
        CancellationToken cancellationToken = default)
    {
        if (GetConversionProblem(sourcePath) is { } problem)
        {
            throw new NotSupportedException(problem);
        }

        if (!SubtitleFormats.TryNormalizeOutputFormat(Path.GetExtension(destinationPath), out var target))
        {
            throw new NotSupportedException($"Can't write {Path.GetExtension(destinationPath)} files. Pick srt, vtt, ass or ssa.");
        }

        // Formats ffmpeg has to read for us land as srt in a scratch file first, and the
        // rest of this treats that as the source.
        string? intermediate = null;

        try
        {
            var readPath = sourcePath;

            if (SubtitleFormats.GetKind(sourcePath) == SubtitleFormatKind.Convertible)
            {
                intermediate = Path.Combine(
                    Path.GetTempPath(),
                    "lapse-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".srt");

                await RunFfmpegAsync(sourcePath, intermediate, cancellationToken).ConfigureAwait(false);
                readPath = intermediate;
            }

            var text = await SubtitleEncoding.ReadAllTextAsync(readPath, cancellationToken).ConfigureAwait(false);
            var cues = Parse(text, readPath);

            if (cues.Count == 0)
            {
                throw new InvalidDataException($"No subtitle cues could be read out of {Path.GetFileName(sourcePath)}.");
            }

            await File.WriteAllTextAsync(destinationPath, Write(cues, target, style), SubtitleEncoding.Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Converted {Source} to {Target} at {Destination} ({Count} cues)",
                Path.GetFileName(sourcePath),
                target,
                destinationPath,
                cues.Count);

            return cues.Count;
        }
        finally
        {
            if (intermediate is not null && File.Exists(intermediate))
            {
                try
                {
                    File.Delete(intermediate);
                }
                catch (IOException ex)
                {
                    // A scratch file we couldn't remove is worth a line in the log and
                    // nothing more - throwing here would lose whatever this call was
                    // actually about, success or failure.
                    _logger.LogDebug(ex, "Could not delete the scratch file {Path}", intermediate);
                }
            }
        }
    }

    private bool HasFfmpeg()
    {
        var path = _mediaEncoder.EncoderPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The input is a validated subtitle path and the output is a scratch file this class just named.")]
    private async Task RunFfmpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -y so a leftover scratch file can't make this hang on a prompt, and the format
        // comes from the .srt extension on the destination.
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(destinationPath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NotSupportedException("Could not start ffmpeg to read that subtitle: " + ex.Message, ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FfmpegTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it finished between the timeout firing and getting here
            }

            throw new TimeoutException("ffmpeg took too long reading that subtitle file.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !File.Exists(destinationPath))
        {
            _logger.LogWarning("ffmpeg could not convert {Source}: {Error}", sourcePath, stderr);
            throw new InvalidDataException(
                $"ffmpeg couldn't read {Path.GetFileName(sourcePath)} as a subtitle file. It may be damaged, or in a format it doesn't handle.");
        }
    }

    // ------------------------------------------------------------------------ reading

    private static List<SubtitleCue> Parse(string text, string path)
    {
        var lines = SubtitleEncoding.SplitLines(text);

        return SubtitleShifter.IsAss(path) ? ParseAss(lines) : ParseTextCues(lines);
    }

    // srt and vtt are the same shape as far as this cares: a timing line with an arrow in
    // it, then the dialogue until a blank line. The cue numbers, vtt's header and its
    // NOTE/STYLE blocks all fall out on their own by only acting on arrow lines.
    private static List<SubtitleCue> ParseTextCues(string[] lines)
    {
        var cues = new List<SubtitleCue>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var matches = TimestampRegex().Matches(lines[i]);
            if (matches.Count < 2)
            {
                continue;
            }

            var cue = new SubtitleCue
            {
                Start = ToTimeSpan(matches[0]),
                End = ToTimeSpan(matches[1])
            };

            for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]); i++)
            {
                cue.Lines.Add(lines[i].TrimEnd());
            }

            if (cue.Lines.Count > 0)
            {
                cues.Add(cue);
            }
        }

        return cues;
    }

    private static List<SubtitleCue> ParseAss(string[] lines)
    {
        var cues = new List<SubtitleCue>();

        // The Format line says which field is which, and files do vary, so read the
        // positions off it rather than trusting the usual order.
        var startField = 1;
        var endField = 2;
        var textField = 9;
        var inEvents = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inEvents = trimmed.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = trimmed[7..].Split(',');
                for (var i = 0; i < names.Length; i++)
                {
                    switch (names[i].Trim().ToLowerInvariant())
                    {
                        case "start": startField = i; break;
                        case "end": endField = i; break;
                        case "text": textField = i; break;
                        default: break;
                    }
                }

                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The text field is last and can hold commas of its own, so the split stops
            // once it has every field before it.
            var fields = trimmed[9..].Split(',', textField + 1);
            if (fields.Length <= Math.Max(textField, Math.Max(startField, endField)))
            {
                continue;
            }

            var start = TimestampRegex().Match(fields[startField]);
            var end = TimestampRegex().Match(fields[endField]);

            if (!start.Success || !end.Success)
            {
                continue;
            }

            var cue = new SubtitleCue
            {
                Start = ToTimeSpan(start),
                End = ToTimeSpan(end)
            };

            var body = AssTagRegex().Replace(fields[textField], string.Empty)
                .Replace("\\h", " ", StringComparison.Ordinal);

            foreach (var part in body.Split(AssLineBreaks, StringSplitOptions.None))
            {
                var value = part.Trim();
                if (value.Length > 0)
                {
                    cue.Lines.Add(value);
                }
            }

            if (cue.Lines.Count > 0)
            {
                cues.Add(cue);
            }
        }

        return cues;
    }

    private static TimeSpan ToTimeSpan(Match match)
    {
        var fraction = match.Groups["f"].Value;

        return new TimeSpan(
            0,
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------------ writing

    private static string Write(List<SubtitleCue> cues, string format, SubtitleStyle? style)
    {
        return format switch
        {
            "vtt" => WriteVtt(cues),
            "ass" => WriteAss(cues, advanced: true, style),
            "ssa" => WriteAss(cues, advanced: false, style),
            _ => WriteSrt(cues)
        };
    }

    private static string WriteSrt(List<SubtitleCue> cues)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < cues.Count; i++)
        {
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatText(cues[i].Start)).Append(" --> ").Append(FormatText(cues[i].End)).Append('\n');

            foreach (var line in cues[i].Lines)
            {
                builder.Append(line).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string WriteVtt(List<SubtitleCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");

        foreach (var cue in cues)
        {
            builder.Append(FormatText(cue.Start, '.')).Append(" --> ").Append(FormatText(cue.End, '.')).Append('\n');

            foreach (var line in cue.Lines)
            {
                builder.Append(line).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string WriteAss(List<SubtitleCue> cues, bool advanced, SubtitleStyle? style)
    {
        // A minimum viable script: enough header for a player to accept the file, one
        // default style, and the events. Styling from a source that had it doesn't
        // survive the trip, which is the deal with converting to a simpler format and
        // back again.
        var builder = new StringBuilder();
        var fields = (style ?? new SubtitleStyle()).ToStyleFields(advanced);

        builder.Append("[Script Info]\n")
            .Append("; Written by the LAPSE Jellyfin plugin\n")
            .Append("ScriptType: ").Append(advanced ? "v4.00+" : "v4.00").Append('\n')
            .Append("WrapStyle: 0\n")
            .Append("ScaledBorderAndShadow: yes\n")
            .Append("PlayResX: 1920\n")
            .Append("PlayResY: 1080\n\n");

        if (advanced)
        {
            builder.Append("[V4+ Styles]\n")
                .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
                .Append("Style: Default,").Append(fields).Append("\n\n")
                .Append("[Events]\n")
                .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        }
        else
        {
            builder.Append("[V4 Styles]\n")
                .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding\n")
                .Append("Style: Default,").Append(fields).Append("\n\n")
                .Append("[Events]\n")
                .Append("Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        }

        var layer = advanced ? "0" : "Marked=0";

        foreach (var cue in cues)
        {
            builder.Append("Dialogue: ").Append(layer).Append(',')
                .Append(FormatAss(cue.Start)).Append(',')
                .Append(FormatAss(cue.End)).Append(",Default,,0,0,0,,")
                .Append(string.Join("\\N", StripMarkup(cue.Lines)))
                .Append('\n');
        }

        return builder.ToString();
    }

    // srt and vtt carry italics and the like as html tags. An ass Dialogue line would
    // show those as literal text, so they come out on the way in.
    private static IEnumerable<string> StripMarkup(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return HtmlTagRegex().Replace(line, string.Empty);
        }
    }

    private static string FormatText(TimeSpan value, char separator = ',')
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}");
    }

    // ass counts in centiseconds behind a single digit hour.
    private static string FormatAss(TimeSpan value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:0}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}");
    }
}
