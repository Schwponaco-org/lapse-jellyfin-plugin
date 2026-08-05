// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Small helpers the engine implementations share for building results.
/// </summary>
public static class EngineResults
{
    /// <summary>
    /// Builds a failure result, preferring whatever the engine wrote to stderr since that
    /// is nearly always more useful than the exit code on its own.
    /// </summary>
    /// <param name="mode">The mode that was requested.</param>
    /// <param name="stderr">Whatever the engine printed to stderr.</param>
    /// <param name="exitCode">Process exit code.</param>
    /// <returns>A failed result.</returns>
    public static SyncResult Failure(SyncMode mode, string stderr, int exitCode)
    {
        var error = string.IsNullOrWhiteSpace(stderr)
            ? string.Format(CultureInfo.InvariantCulture, "Engine exited with code {0}", exitCode)
            : stderr.Trim();

        return new SyncResult { Success = false, Mode = mode, Error = error };
    }

    /// <summary>
    /// Trims an engine's output down to something short enough to show in a toast or an
    /// alert. Some engines are chatty and we only want the tail end of it.
    /// </summary>
    /// <param name="text">The raw output.</param>
    /// <param name="maxLength">How much to keep.</param>
    /// <returns>The trimmed text, or null if there was nothing useful.</returns>
    public static string? Summarize(string text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var lastLine = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();

        return lastLine.Length > maxLength ? lastLine[..maxLength] : lastLine;
    }
}
