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
/// Reads subtitle files without assuming UTF-8.
///
/// Lots of subs aren't (Arabic is usually 1256, Russian 1251, Greek 1253, old Western
/// ones 1252). Reading those as UTF-8 silently turns letters into question marks, and the
/// next write makes it permanent. So: BOM if there is one, UTF-8 if the bytes are valid
/// UTF-8, otherwise guess the legacy code page that makes most sense. New files go out as
/// UTF-8 without BOM, files edited in place keep their encoding (<see cref="ReadDocumentAsync"/>).
/// </summary>
public static class SubtitleEncoding
{
    // code pages to try, each with the Unicode block its text should land in if it's the right guess
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
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    /// <summary>
    /// Reads a subtitle file as lines, working out its encoding first.
    /// </summary>
    public static async Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return SplitLines(text);
    }

    /// <summary>
    /// Reads a file for editing in place, keeping its encoding, BOM and line endings so a
    /// shift only changes the numbers (some players choke on a quietly re-encoded file).
    /// </summary>
    public static async Task<SubtitleDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (text, encoding) = DecodeWithEncoding(bytes);

        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var endsWithNewLine = text.EndsWith('\n') || text.EndsWith('\r');
        var lines = SplitLines(text);

        // a trailing newline splits into one empty line too many
        if (endsWithNewLine && lines.Length > 0)
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        return new SubtitleDocument(lines, encoding, crlf ? "\r\n" : "\n", endsWithNewLine);
    }

    /// <summary>
    /// Turns lines back into the bytes of a file shaped like the one they were read from.
    /// </summary>
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
    public static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Gets what we write new files in: UTF-8 without BOM, which every player agrees on.
    /// </summary>
    public static UTF8Encoding Utf8NoBom { get; } = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Turns subtitle file bytes into text.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        return DecodeWithEncoding(bytes).Text;
    }

    /// <summary>
    /// Turns bytes into text and says which encoding it was (with BOM if the file had one).
    /// </summary>
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

        // valid UTF-8 is basically never anything else, no guessing needed
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return (Encoding.UTF8.GetString(bytes), Utf8NoBom);
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

        // 1252 maps every byte, so we always end up with something
        return best is not null && bestEncoding is not null
            ? (best, bestEncoding)
            : (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
    }

    // + for text inside the script's block, - for signs of a wrong guess (replacement chars, C1 controls)
    private static double Score(string text, char rangeStart, char rangeEnd)
    {
        var inScript = 0;
        var wrong = 0;
        var counted = 0;

        foreach (var c in text)
        {
            if (c < 0x80)
            {
                // ASCII is the same in all of these, tells us nothing
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
            // pure ASCII, they're all the same
            return 0;
        }

        return ((double)inScript / counted) - ((double)wrong / counted);
    }

    private static void EnsureCodePagesRegistered()
    {
        // .NET only has Unicode and Latin-1 built in, the code pages need this provider
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}

/// <summary>
/// A subtitle read for editing in place, with what's needed to write it back the same way.
/// </summary>
public sealed record SubtitleDocument(IReadOnlyList<string> Lines, Encoding Encoding, string NewLine, bool EndsWithNewLine);
