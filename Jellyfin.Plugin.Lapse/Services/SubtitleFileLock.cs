// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Keeps two jobs from rewriting the same subtitle file at once. A shift reads the whole
/// file, changes it and writes it back, and so does a sync. Two of those overlapping -
/// a double press on the nudge buttons, or a shift while the queue is syncing the same
/// file - used to end with one reading the file while the other had it half written,
/// and an empty subtitle where a working one had been.
/// </summary>
public static class SubtitleFileLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for exclusive use of a subtitle file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dispose it to let the next job in.</returns>
    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    /// <summary>
    /// Writes lines to a file by way of a temporary file beside it, so anything reading
    /// the file sees either the old contents or the new ones and never a half written
    /// one.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="lines">The contents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers pass a path they already validated against the library, or one derived from it with a fixed suffix.")]
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        var document = new SubtitleDocument(Array.Empty<string>(), SubtitleEncoding.Utf8NoBom, Environment.NewLine, true);
        return WriteBytesAsync(path, SubtitleEncoding.Encode(document, new List<string>(lines)), cancellationToken);
    }

    /// <summary>
    /// Writes edited lines back the way the file they came from was written: same
    /// encoding, same byte order mark, same line endings. Goes through a temporary file
    /// the same way <see cref="WriteAllLinesAsync"/> does.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="document">What the source was read as.</param>
    /// <param name="lines">The edited lines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public static Task WriteDocumentAsync(string path, SubtitleDocument document, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        return WriteBytesAsync(path, SubtitleEncoding.Encode(document, lines), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Callers pass a path they already validated against the library, or one derived from it with a fixed suffix.")]
    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = path + ".lapse-write-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // nothing more to do about a stray temp file than leave it
                }
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
