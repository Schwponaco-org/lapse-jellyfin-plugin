// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// A subtitle file split into "the lines that are dialogue" and "everything else".
/// Translation only ever touches the dialogue, so timings, cue numbers, style blocks and
/// headers come back out exactly as they went in.
/// </summary>
public class SubtitleTextFile
{
    private readonly List<string> _lines;
    private readonly List<TextSpan> _spans;

    private SubtitleTextFile(List<string> lines, List<TextSpan> spans, string extension)
    {
        _lines = lines;
        _spans = spans;
        Extension = extension;
    }

    /// <summary>
    /// Gets the file's extension, lowercased, including the dot.
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// Gets the dialogue lines, in file order.
    /// </summary>
    public IReadOnlyList<string> TextLines => _spans.Select(s => s.Text).ToList();

    /// <summary>
    /// Reads a subtitle file and works out which of its lines are dialogue.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="NotSupportedException">Thrown for a format we can't pick text out of.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers only pass subtitle paths the library reported for an item, or paths an admin picked in the file browser.")]
    public static async Task<SubtitleTextFile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var lines = (await SubtitleEncoding.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).ToList();

        var spans = extension switch
        {
            ".srt" or ".vtt" => FindCueText(lines),
            ".ass" or ".ssa" => FindDialogueText(lines),
            _ => throw new NotSupportedException($"Translation works on .srt, .vtt, .ass and .ssa files, not {extension}")
        };

        return new SubtitleTextFile(lines, spans, extension);
    }

    /// <summary>
    /// Puts translated text back in place of the original dialogue.
    /// </summary>
    /// <param name="translated">One entry per dialogue line, in the same order as
    /// <see cref="TextLines"/>. Null entries leave that line alone.</param>
    public void ApplyTranslations(IReadOnlyList<string?> translated)
    {
        for (var i = 0; i < _spans.Count && i < translated.Count; i++)
        {
            var replacement = translated[i];
            if (replacement is null)
            {
                continue;
            }

            var span = _spans[i];
            _lines[span.LineIndex] = span.Prefix + replacement;
        }
    }

    /// <summary>
    /// Writes the file back out, optionally with a comment block at the top.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="headerLines">Lines to put at the very top, already formatted as
    /// comments for this format, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The output path is derived from an input path the caller already validated, with a fixed suffix.")]
    public async Task SaveAsync(string path, IReadOnlyList<string>? headerLines, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();

        if (headerLines is { Count: > 0 })
        {
            output.AddRange(headerLines.Select(CommentFor));
            output.Add(string.Empty);
        }

        output.AddRange(_lines);

        await File.WriteAllLinesAsync(path, output, SubtitleEncoding.Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the name of the translated file: Movie.en.srt with target "es" becomes
    /// Movie.es.translated.srt. An existing language tag on the end of the name gets
    /// replaced rather than stacked, so re-translating doesn't grow the name each time.
    /// </summary>
    /// <param name="sourcePath">The subtitle being translated.</param>
    /// <param name="targetLanguage">The language code being translated into.</param>
    /// <returns>The output path.</returns>
    public static string BuildOutputPath(string sourcePath, string targetLanguage)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var extension = Path.GetExtension(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        // drop a trailing ".translated" from an earlier run, then a trailing language tag
        if (stem.EndsWith(".translated", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^".translated".Length];
        }

        var lastDot = stem.LastIndexOf('.');
        if (lastDot > 0)
        {
            var tag = stem[(lastDot + 1)..];
            if (tag.Length is >= 2 and <= 3 && tag.All(char.IsLetter))
            {
                stem = stem[..lastDot];
            }
        }

        return Path.Combine(directory, $"{stem}.{targetLanguage}.translated{extension}");
    }

    private string CommentFor(string text)
    {
        // ASS/SSA use ; for comments, WebVTT uses NOTE, and SRT has no comment syntax at
        // all - a NOTE block at the top is ignored by every player worth worrying about
        // because it isn't a numbered cue.
        return Extension is ".ass" or ".ssa" ? "; " + text : "NOTE " + text;
    }

    // srt and vtt: a cue is "optional number / timing line / one or more text lines".
    // Anything that isn't a timing line, a bare cue number, a blank, or a WEBVTT/NOTE
    // header is dialogue.
    private static List<TextSpan> FindCueText(IReadOnlyList<string> lines)
    {
        var spans = new List<TextSpan>();
        var inNoteBlock = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                inNoteBlock = false;
                continue;
            }

            if (inNoteBlock)
            {
                continue;
            }

            if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("STYLE", StringComparison.Ordinal)
                || trimmed.StartsWith("REGION", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("NOTE", StringComparison.Ordinal))
            {
                inNoteBlock = true;
                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            if (int.TryParse(trimmed, out _))
            {
                continue;
            }

            spans.Add(new TextSpan(i, string.Empty, line));
        }

        return spans;
    }

    // ass/ssa: only Dialogue: lines carry text, and the text is everything after the
    // ninth comma. The nine fields before it are Layer, Start, End, Style, Name, MarginL,
    // MarginR, MarginV and Effect.
    private static List<TextSpan> FindDialogueText(IReadOnlyList<string> lines)
    {
        const int FieldsBeforeText = 9;
        var spans = new List<TextSpan>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var commas = 0;
            var textStart = -1;

            for (var c = 0; c < line.Length; c++)
            {
                if (line[c] != ',')
                {
                    continue;
                }

                commas++;
                if (commas == FieldsBeforeText)
                {
                    textStart = c + 1;
                    break;
                }
            }

            if (textStart > 0 && textStart < line.Length)
            {
                spans.Add(new TextSpan(i, line[..textStart], line[textStart..]));
            }
        }

        return spans;
    }

    private sealed class TextSpan
    {
        public TextSpan(int lineIndex, string prefix, string text)
        {
            LineIndex = lineIndex;
            Prefix = prefix;
            Text = text;
        }

        public int LineIndex { get; }

        public string Prefix { get; }

        public string Text { get; }
    }
}
