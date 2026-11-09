// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Tidying up after the real work. A file that won't go away is a stray file, not a reason
/// to fail what already happened or to bury the error that got us here.
/// </summary>
internal static class QuietFile
{
    /// <summary>
    /// Deletes a file if it's there. Returns whether it was deleted.
    /// </summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left where it is, that's fine
        }

        return false;
    }
}
