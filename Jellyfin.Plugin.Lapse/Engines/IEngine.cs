// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// One sync engine. Engines differ in how they take their arguments and what they print
/// when they're done, so each one gets its own small implementation.
/// </summary>
public interface IEngine
{
    /// <summary>
    /// Gets the static info about this engine.
    /// </summary>
    EngineDescriptor Descriptor { get; }

    /// <summary>
    /// Builds the command line arguments for one run.
    /// </summary>
    /// <param name="referencePath">The video (or reference subtitle) to line up against.</param>
    /// <param name="inputPath">The subtitle that needs fixing.</param>
    /// <param name="outputPath">Where the fixed subtitle should be written.</param>
    /// <param name="mode">Which alignment mode to use.</param>
    /// <param name="penalty">Penalty value, only meaningful for split mode.</param>
    /// <returns>The arguments, in order.</returns>
    IReadOnlyList<string> BuildArguments(string referencePath, string inputPath, string outputPath, SyncMode mode, int penalty);

    /// <summary>
    /// Reads whatever the engine printed and works out what happened.
    /// </summary>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="requestedMode">The mode we asked for, used when the output doesn't say.</param>
    /// <param name="requestedPenalty">The penalty we passed, so a result can report it back.</param>
    /// <returns>The parsed result.</returns>
    SyncResult ParseResult(string stdout, string stderr, int exitCode, SyncMode requestedMode, int requestedPenalty);
}
