// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// A subtitle split into dialogue lines and everything else. Translation only touches the
/// dialogue so timings, numbers, styles and headers come out untouched.
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
    /// Gets the dialogue lines in file order. Builds a new list each time, grab it once.
    /// </summary>
    public IReadOnlyList<string> TextLines => _spans.ConvertAll(s => s.Text);

    /// <summary>
    /// Reads a subtitle file and works out which of its lines are dialogue.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown for a format we can't pick text out of.</exception>
    public static async Task<SubtitleTextFile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var lines = (await SubtitleEncoding.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).ToList();

        // trailing newline gives one empty line too many
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

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
    public async Task SaveAsync(string path, IReadOnlyList<string>? headerLines, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        var body = _lines;

        if (headerLines is { Count: > 0 } && SupportsComments)
        {
            // vtt must start with WEBVTT, so the NOTE goes below it. Everything else gets it on top.
            var signature = _lines.FindIndex(line => line.Trim().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase));

            if (signature >= 0)
            {
                output.AddRange(_lines.Take(signature + 1));
                output.Add(string.Empty);
                body = _lines.Skip(signature + 1).SkipWhile(string.IsNullOrWhiteSpace).ToList();
            }

            output.AddRange(headerLines.Select(CommentFor));
            output.Add(string.Empty);
        }

        output.AddRange(body);

        await SubtitleFileLock.WriteAllLinesAsync(path, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the translated file name: Movie.en.srt + "es" = Movie.es.translated.srt.
    /// An existing language tag gets replaced, not stacked.
    /// </summary>
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

        if (TryGetLanguageTag(sourcePath, out _, out var lastDot))
        {
            stem = stem[..lastDot];
        }

        // typed by a person and goes in a file name, so only tag characters ("pt-BR", "zh_Hant"), no slashes
        var tag = new string(targetLanguage.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (tag.Length == 0)
        {
            tag = "xx";
        }

        return Path.Combine(directory, $"{stem}.{tag}.translated{extension}");
    }

    /// <summary>
    /// Reads the language tag off a subtitle's name (Movie.en.srt gives "en"). Releases
    /// nearly always name them like that, and providers that reject "auto" need a real code.
    /// </summary>
    public static bool TryGetLanguageTag(string path, out string language)
    {
        return TryGetLanguageTag(path, out language, out _);
    }

    private static bool TryGetLanguageTag(string path, out string language, out int tagStart)
    {
        language = string.Empty;
        tagStart = -1;

        var stem = Path.GetFileNameWithoutExtension(path);

        if (stem.EndsWith(".translated", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^".translated".Length];
        }

        var lastDot = stem.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return false;
        }

        var tag = stem[(lastDot + 1)..];
        if (tag.Length is < 2 or > 3 || !tag.All(char.IsLetter))
        {
            return false;
        }

        language = tag.ToLowerInvariant();
        tagStart = lastDot;
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether the format has comments for a header. srt doesn't, a
    /// NOTE block there made strict players drop the first cue or reject the whole file.
    /// </summary>
    public bool SupportsComments => Extension is ".ass" or ".ssa" or ".vtt" or ".webvtt";

    private string CommentFor(string text)
    {
        // ASS/SSA use ; for comments, WebVTT uses NOTE.
        return Extension is ".ass" or ".ssa" ? "; " + text : "NOTE " + text;
    }

    // srt/vtt: dialogue is whatever follows a timing line up to the next blank line. That
    // keeps out cue numbers, the WEBVTT header and NOTE/STYLE/REGION blocks (a translated
    // STYLE block is a stylesheet in Spanish). Going by position also lets a line that's
    // just "1984" through instead of mistaking it for a cue number.
    private static List<TextSpan> FindCueText(IReadOnlyList<string> lines)
    {
        var spans = new List<TextSpan>();
        var inCue = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (line.Trim().Length == 0)
            {
                inCue = false;
                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                inCue = true;
                continue;
            }

            // no blank line between cues, this is the next cue's number
            if (i + 1 < lines.Count && lines[i + 1].Contains("-->", StringComparison.Ordinal))
            {
                inCue = false;
                continue;
            }

            if (inCue)
            {
                spans.Add(new TextSpan(i, string.Empty, line));
            }
        }

        return spans;
    }

    // ass/ssa: text is everything after the 9th comma of a Dialogue line
    // (Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect)
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

    private sealed record TextSpan(int LineIndex, string Prefix, string Text);
}
