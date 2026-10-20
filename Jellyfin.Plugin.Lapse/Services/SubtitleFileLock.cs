// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// One writer per subtitle file at a time. Two overlapping read-modify-writes (double
/// pressing the nudge button, or a shift during a queue sync) used to leave an empty file.
/// </summary>
public static class SubtitleFileLock
{
    // one gate per file in use, removed again when nobody holds or waits on it
    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for exclusive use of a subtitle file.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(path);
        Gate gate;

        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out gate!))
            {
                gate = new Gate();
                Gates[key] = gate;
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Leave(key, gate);
            throw;
        }

        return new Releaser(key, gate);
    }

    // For tests. Asks about one file, a global count races with other tests running in parallel.
    internal static bool HasGate(string path)
    {
        lock (Gates)
        {
            return Gates.ContainsKey(Path.GetFullPath(path));
        }
    }

    private static void Leave(string key, Gate gate)
    {
        lock (Gates)
        {
            if (--gate.Users == 0)
            {
                Gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes lines via a temp file next to it, so readers never see a half written file.
    /// </summary>
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        var document = new SubtitleDocument(Array.Empty<string>(), SubtitleEncoding.Utf8NoBom, Environment.NewLine, true);
        return WriteBytesAsync(path, SubtitleEncoding.Encode(document, new List<string>(lines)), cancellationToken);
    }

    /// <summary>
    /// Writes text as UTF-8 (no BOM) via a temp file.
    /// </summary>
    public static Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        return WriteBytesAsync(path, SubtitleEncoding.Utf8NoBom.GetBytes(text), cancellationToken);
    }

    /// <summary>
    /// Writes edited lines back with the original encoding, BOM and line endings, via a temp file.
    /// </summary>
    public static Task WriteDocumentAsync(string path, SubtitleDocument document, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        return WriteBytesAsync(path, SubtitleEncoding.Encode(document, lines), cancellationToken);
    }

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
                    // oh well, a stray temp file
                }
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        // holders + waiters, counted under the dictionary lock
        public int Users { get; set; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private Gate? _gate;

        public Releaser(string key, Gate gate)
        {
            _key = key;
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                gate.Semaphore.Release();
                Leave(_key, gate);
            }
        }
    }
}
