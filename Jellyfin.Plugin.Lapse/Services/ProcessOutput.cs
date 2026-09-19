// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Shared setup for reading what a child process prints.
///
/// .NET decodes a redirected pipe with the console's code page, and a Jellyfin server
/// started without a console falls back to the machine's ANSI one - 936 on a Chinese
/// Windows install, 1252 on a Western one. The engines and ffmpeg all write UTF-8, so
/// unless we say so, every non-ASCII path they mention comes back mangled. That is worse
/// than cosmetic: a UTF-8 byte pair read as one code page 936 character can swallow the
/// backslash that followed it, which turns the engine's JSON into something that no
/// longer parses and a finished sync into "the output didn't look like a normal result".
/// </summary>
public static class ProcessOutput
{
    // No BOM: this decodes a stream rather than writing a file, and a stray U+FEFF at the
    // front of the first line would only get in the way of parsing it.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads a process's stdout and stderr as UTF-8, whatever the server's code page is.
    /// Both streams have to already be redirected.
    /// </summary>
    /// <param name="startInfo">The process about to be started.</param>
    public static void ReadAsUtf8(ProcessStartInfo startInfo)
    {
        startInfo.StandardOutputEncoding = Utf8;
        startInfo.StandardErrorEncoding = Utf8;
    }
}
