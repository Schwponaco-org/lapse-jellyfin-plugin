// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Makes child process output read as UTF-8.
///
/// Without a console .NET decodes pipes with the machine's ANSI code page (936 on Chinese
/// Windows, 1252 on Western). Engines write UTF-8, so non-ASCII paths got mangled, and on
/// 936 a byte pair could eat the following backslash and break the engine's JSON.
/// </summary>
public static class ProcessOutput
{
    // no BOM, a stray U+FEFF would just get in the way of parsing
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Sets stdout and stderr to UTF-8. Both must already be redirected.
    /// </summary>
    public static void ReadAsUtf8(ProcessStartInfo startInfo)
    {
        startInfo.StandardOutputEncoding = Utf8;
        startInfo.StandardErrorEncoding = Utf8;
    }
}
