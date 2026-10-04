// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Reads subtitle files without assuming they're UTF-8.
///
/// Plenty of subtitles in the wild aren't. Arabic ones are usually Windows-1256, Russian
/// ones Windows-1251, Greek ones Windows-1253, and older Western European ones
/// Windows-1252. Reading those as UTF-8 doesn't fail loudly, it just replaces every byte
/// it doesn't understand with a question mark, and anything written back out afterwards -
/// a shift, a translation, a conversion - saves that damage permanently.
///
/// So: honour a byte order mark if there is one, take UTF-8 when the bytes really are
/// valid UTF-8, and otherwise work out which legacy code page makes the most sense of the
/// file. A new file the plugin writes goes out as UTF-8 without a BOM, which every player
/// and browser reads correctly regardless of the language or its writing direction. A file
/// edited in place - a shift by hand - goes back out the way it came in, see
/// <see cref="ReadDocumentAsync"/>.
/// </summary>
public static class SubtitleEncoding
{
    // Code pages worth trying, in the order they're most likely to turn up. Each one is
    // paired with the Unicode block its text should land in when the guess is right.
    private static readonly (int CodePage, char RangeStart, char RangeEnd)[] Candidates =
    {
        (1256, '؀', 'ۿ'), // Arabic
        (1251, 'Ѐ', 'ӿ'), // Cyrillic
        (1253, 'Ͱ', 'Ͽ'), // Greek
        (1255, '֐', '׿'), // Hebrew
        (874, '฀', '๿'),  // Thai
        (1254, 'À', 'ſ'), // Turkish
        (1250, 'À', 'ſ'), // Central European
        (1252, 'À', 'ÿ')  // Western European
    };

    private static int _providerRegistered;

    /// <summary>
    /// Reads a subtitle file as text, working out its encoding first.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file's contents.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers pass a subtitle path the library reported for an item, or one an admin picked in the file browser.")]
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    /// <summary>
    /// Reads a subtitle file as lines, working out its encoding first.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file's lines, with the line endings taken off.</returns>
    public static async Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return SplitLines(text);
    }

    /// <summary>
    /// Reads a subtitle file for editing in place: its lines, plus what it takes to write
    /// them back the way the file was, encoding, byte order mark and line endings
    /// included. A shift moves a few digits, and turning a Windows-1251 file into UTF-8 or
    /// its CRLF endings into LF on the way through is a change nobody asked for, which some
    /// players then read wrongly.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file, ready to be edited and written back.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers pass a subtitle path the library reported for an item.")]
    public static async Task<SubtitleDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (text, encoding) = DecodeWithEncoding(bytes);

        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var endsWithNewLine = text.EndsWith('\n') || text.EndsWith('\r');
        var lines = SplitLines(text);

        // A file that ends with a line break splits into one empty line too many, which
        // would come back as an extra blank line on every save.
        if (endsWithNewLine && lines.Length > 0)
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        return new SubtitleDocument(lines, encoding, crlf ? "\r\n" : "\n", endsWithNewLine);
    }

    /// <summary>
    /// Turns lines back into the bytes of a file shaped like the one they were read from.
    /// </summary>
    /// <param name="document">What the file was read as.</param>
    /// <param name="lines">The lines to write.</param>
    /// <returns>The file contents.</returns>
    public static byte[] Encode(SubtitleDocument document, IReadOnlyList<string> lines)
    {
        var text = string.Join(document.NewLine, lines) + (document.EndsWithNewLine ? document.NewLine : string.Empty);
        var preamble = document.Encoding.GetPreamble();
        var body = document.Encoding.GetBytes(text);

        if (preamble.Length == 0)
        {
            return body;
        }

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>
    /// Splits text into lines on any of the three line ending conventions.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines.</returns>
    public static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Gets the encoding everything written by the plugin uses: UTF-8, no byte order
    /// mark. Right for every language, and the one thing every player agrees on.
    /// </summary>
    public static UTF8Encoding Utf8NoBom { get; } = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Turns subtitle file bytes into text.
    /// </summary>
    /// <param name="bytes">The raw file.</param>
    /// <returns>The decoded text, with any byte order mark removed.</returns>
    public static string Decode(byte[] bytes)
    {
        return DecodeWithEncoding(bytes).Text;
    }

    /// <summary>
    /// Turns subtitle file bytes into text, and says which encoding that took, so the
    /// file can be written back in it. The encoding carries the byte order mark when the
    /// file had one.
    /// </summary>
    /// <param name="bytes">The raw file.</param>
    /// <returns>The decoded text, with any byte order mark removed, and its encoding.</returns>
    public static (string Text, Encoding Encoding) DecodeWithEncoding(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return (string.Empty, Utf8NoBom);
        }

        if (TryDecodeByteOrderMark(bytes) is { } fromBom)
        {
            return fromBom;
        }

        // Real UTF-8 is unambiguous enough that valid UTF-8 is essentially never anything
        // else, so this is the one check that doesn't have to guess.
        if (TryDecodeStrictUtf8(bytes) is { } utf8)
        {
            return (utf8, Utf8NoBom);
        }

        return DecodeLegacy(bytes);
    }

    private static (string Text, Encoding Encoding)? TryDecodeByteOrderMark(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
        }

        return null;
    }

    private static string? TryDecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static (string Text, Encoding Encoding) DecodeLegacy(byte[] bytes)
    {
        EnsureCodePagesRegistered();

        string? best = null;
        Encoding? bestEncoding = null;
        var bestScore = double.MinValue;

        foreach (var (codePage, rangeStart, rangeEnd) in Candidates)
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(codePage);
            }
            catch (ArgumentException)
            {
                // this platform doesn't carry that code page
                continue;
            }

            var text = encoding.GetString(bytes);
            var score = Score(text, rangeStart, rangeEnd);

            if (score > bestScore)
            {
                bestScore = score;
                best = text;
                bestEncoding = encoding;
            }
        }

        // Windows-1252 maps every byte to something, so there is always an answer here.
        return best is not null && bestEncoding is not null
            ? (best, bestEncoding)
            : (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
    }

    // Rewards text that lands inside one script's own block and punishes the giveaways of
    // a wrong guess: replacement characters, unassigned slots and stray control codes.
    private static double Score(string text, char rangeStart, char rangeEnd)
    {
        var inScript = 0;
        var wrong = 0;
        var counted = 0;

        foreach (var c in text)
        {
            if (c < 0x80)
            {
                // Timestamps, tags and cue numbers are ASCII in every one of these
                // encodings, so they say nothing about which is right.
                continue;
            }

            counted++;

            if (c >= rangeStart && c <= rangeEnd)
            {
                inScript++;
            }
            else if (c == '�' || char.IsControl(c) || (c >= '' && c <= ''))
            {
                wrong++;
            }
        }

        if (counted == 0)
        {
            // Nothing but ASCII: every candidate decodes it identically, so the first one
            // asked wins and it doesn't matter which.
            return 0;
        }

        return ((double)inScript / counted) - ((double)wrong / counted);
    }

    private static void EnsureCodePagesRegistered()
    {
        // .NET Core ships only the Unicode encodings and Latin-1 out of the box; the
        // legacy code pages come from a provider that has to be registered once.
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}

/// <summary>
/// A subtitle file read for editing in place, with what it takes to write it back the way
/// it was.
/// </summary>
/// <param name="Lines">The file's lines, without their line endings.</param>
/// <param name="Encoding">The encoding it was in, with its byte order mark if it had one.</param>
/// <param name="NewLine">The line ending it used.</param>
/// <param name="EndsWithNewLine">Whether the last line had a line ending after it.</param>
public sealed record SubtitleDocument(IReadOnlyList<string> Lines, Encoding Encoding, string NewLine, bool EndsWithNewLine);
