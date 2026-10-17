// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    // dialogue lines, markup already stripped
    public List<string> Lines { get; } = new();
}

/// <summary>
/// What a styled conversion wrote, and what it found while writing it.
/// </summary>
public class StyledConversionResult
{
    /// <summary>
    /// Gets or sets how many cues came across.
    /// </summary>
    public int Cues { get; set; }

    /// <summary>
    /// Gets or sets the writing system the dialogue turned out to be in.
    /// </summary>
    public SubtitleScript Script { get; set; }

    /// <summary>
    /// Gets or sets the style actually written after fitting it to the script. Null when none was asked for.
    /// </summary>
    public SubtitleStyle? Style { get; set; }
}

/// <summary>
/// Turns a subtitle file into another format.
///
/// srt, vtt, ass and ssa are plain text so we read and write those ourselves, no point
/// starting a process for some string handling. Other text formats (MicroDVD, SAMI,
/// SubViewer...) go through Jellyfin's ffmpeg and come back as srt. Picture formats
/// (PGS, VobSub) have no text at all, so those get turned away with a reason.
/// </summary>
public partial class SubtitleConverter
{
    /// <summary>
    /// Header comment that marks a file we restyled, so it can be told apart from a plain conversion later.
    /// </summary>
    public const string ReadableMarker = "; LAPSE-Readable: 1";

    // 00:01:23,456 / 00:01:23.456 / 01:23.456 (vtt, no hours) / 0:01:23.45
    [GeneratedRegex(@"(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})[,.](?<f>\d{1,3})")]
    private static partial Regex TimestampRegex();

    // vtt-only markup (voice/class spans, ruby, lang, karaoke timestamps). Other formats would show it as text.
    [GeneratedRegex(@"</?(?:c|v|lang|ruby|rt)(?:[.\s][^>]*)?>|<(?:\d{1,3}:)?\d{2}:\d{2}\.\d{3}>")]
    private static partial Regex VttOnlyTagRegex();

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
    public SubtitleConverter(IMediaEncoder mediaEncoder, ILogger<SubtitleConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Says why a file can't be converted, or null when it can be.
    /// </summary>
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
    /// <exception cref="NotSupportedException">The source isn't a format that can be converted.</exception>
    /// <exception cref="InvalidDataException">The source held no cues.</exception>
    public async Task<int> ConvertAsync(
        string sourcePath,
        string destinationPath,
        SubtitleStyle? style = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ConvertStyledAsync(sourcePath, destinationPath, style, null, cancellationToken)
            .ConfigureAwait(false);

        return result.Cues;
    }

    /// <summary>
    /// Converts a subtitle and fits the style to the script the dialogue is in. The text has
    /// to be read first: a Latin-only font is useless for Arabic, letter spacing breaks
    /// joined scripts, and RTL has to be marked in the file.
    /// </summary>
    /// <exception cref="NotSupportedException">The source isn't a format that can be converted.</exception>
    /// <exception cref="InvalidDataException">The source held no cues.</exception>
    public async Task<StyledConversionResult> ConvertStyledAsync(
        string sourcePath,
        string destinationPath,
        SubtitleStyle? style,
        string? nonLatinFontName,
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

        // formats only ffmpeg reads go to a scratch srt first and we work from that
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

            if (target != "vtt" && string.Equals(Path.GetExtension(readPath), ".vtt", StringComparison.OrdinalIgnoreCase))
            {
                CleanVttMarkup(cues);
            }

            if (cues.Count == 0)
            {
                throw new InvalidDataException($"No subtitle cues could be read out of {Path.GetFileName(sourcePath)}.");
            }

            // only the dialogue, timestamps would make everything look Latin
            var script = SubtitleScripts.Detect(JoinDialogue(cues));
            var fitted = style?.ForScript(script, nonLatinFontName);

            // via a temp file so a library scan or a sync never reads half a subtitle
            await SubtitleFileLock.WriteTextAsync(destinationPath, Write(cues, target, fitted), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Converted {Source} to {Target} at {Destination} ({Count} cues, {Script})",
                Path.GetFileName(sourcePath),
                target,
                destinationPath,
                cues.Count,
                SubtitleScripts.GetName(script));

            return new StyledConversionResult
            {
                Cues = cues.Count,
                Script = script,
                Style = fitted
            };
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
                    // don't throw from finally over a temp file
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

    // plenty to tell one alphabet from another, no need to read the whole film
    private const int ScriptSampleChars = 20000;

    private static string JoinDialogue(List<SubtitleCue> cues)
    {
        var builder = new StringBuilder();

        foreach (var cue in cues)
        {
            foreach (var line in cue.Lines)
            {
                builder.Append(line).Append(' ');
            }

            if (builder.Length >= ScriptSampleChars)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private async Task RunFfmpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        // -y so a leftover can't hang on a prompt. Output format comes from the .srt
        // extension, and since we named the temp file there's no protocol to worry about.
        var arguments = new[] { "-nostdin", "-y", "-i", FfmpegPaths.AsFileUrl(sourcePath), destinationPath };

        int exitCode;
        string stderr;

        try
        {
            (exitCode, stderr) = await FfmpegPaths.RunAsync(_mediaEncoder.EncoderPath, arguments, FfmpegTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("ffmpeg took too long reading that subtitle file.", ex);
        }

        if (exitCode != 0 || !File.Exists(destinationPath))
        {
            _logger.LogWarning("ffmpeg could not convert {Source}: {Error}", sourcePath, stderr);
            throw new InvalidDataException(
                $"ffmpeg couldn't read {Path.GetFileName(sourcePath)} as a subtitle file. It may be damaged, or in a format it doesn't handle.");
        }
    }


    private static List<SubtitleCue> Parse(string text, string path)
    {
        var lines = SubtitleEncoding.SplitLines(text);

        return SubtitleShifter.IsAss(path) ? ParseAss(lines) : ParseTextCues(lines);
    }

    // srt and vtt look the same from here: an arrow line, then dialogue until a blank line.
    // Cue numbers, the vtt header and NOTE/STYLE blocks drop out by only acting on arrow lines.
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

        // read the field positions off the Format line, files don't all use the usual order
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

            // text is last and can contain commas, so stop splitting there
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

    // undo vtt's html escapes and drop its own markup nothing else understands
    private static void CleanVttMarkup(List<SubtitleCue> cues)
    {
        foreach (var cue in cues)
        {
            for (var i = 0; i < cue.Lines.Count; i++)
            {
                cue.Lines[i] = VttOnlyTagRegex().Replace(cue.Lines[i], string.Empty)
                    .Replace("&nbsp;", "\u00A0", StringComparison.Ordinal)
                    .Replace("&lrm;", "\u200E", StringComparison.Ordinal)
                    .Replace("&rlm;", "\u200F", StringComparison.Ordinal)
                    .Replace("&lt;", "<", StringComparison.Ordinal)
                    .Replace("&gt;", ">", StringComparison.Ordinal)
                    .Replace("&amp;", "&", StringComparison.Ordinal);
            }

            cue.Lines.RemoveAll(string.IsNullOrWhiteSpace);
        }

        cues.RemoveAll(c => c.Lines.Count == 0);
    }

    private static TimeSpan ToTimeSpan(Match match)
    {
        var fraction = match.Groups["f"].Value;

        return new TimeSpan(
            0,
            match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0,
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture));
    }


    private static string Write(List<SubtitleCue> cues, string format, SubtitleStyle? style)
    {
        // Players sort ass events themselves, but plenty get out of order srt/vtt wrong.
        // OrderBy is stable so cues at the same time keep their order.
        if (format is not ("ass" or "ssa"))
        {
            cues = cues.OrderBy(c => c.Start).ToList();
        }

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
        // Bare minimum: enough header for players to accept it, one Default style, the
        // events. Styling from the source doesn't survive, that's the deal with converting.
        var builder = new StringBuilder();
        var fields = (style ?? new SubtitleStyle()).ToStyleFields(advanced);

        builder.Append("[Script Info]\n")
            .Append("; Written by the LAPSE Jellyfin plugin\n");

        // a style means someone pressed Readable Subtitles, mark it so it can be undone later
        if (style is not null)
        {
            builder.Append(ReadableMarker).Append('\n');
        }

        builder.Append("ScriptType: ").Append(advanced ? "v4.00+" : "v4.00").Append('\n')
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

    // srt/vtt italics etc are html tags, ass would show them as literal text
    private static IEnumerable<string> StripMarkup(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return HtmlTagRegex().Replace(line, string.Empty);
        }
    }

    private static string FormatText(TimeSpan value, char separator = ',')
    {
        return SubtitleShifter.Format(value, separator.ToString(), 2, 3);
    }

    // ass is h:mm:ss.cc, rounded like the engine does
    private static string FormatAss(TimeSpan value)
    {
        return SubtitleShifter.Format(value, ".", 1, 2);
    }
}
