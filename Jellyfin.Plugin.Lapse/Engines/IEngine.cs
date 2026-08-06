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
    /// <param name="options">Paths, mode, and what the installed binary supports.</param>
    /// <returns>The arguments, in order.</returns>
    IReadOnlyList<string> BuildArguments(EngineRunOptions options);

    /// <summary>
    /// Says whether the runner has to copy the input subtitle to the output path before
    /// starting the engine. Engines that rewrite the file they're pointed at need that;
    /// engines that take a real output argument don't, and for those an untouched output
    /// path keeps "did the engine actually write anything" a meaningful check.
    /// </summary>
    /// <param name="runtime">What the installed binary supports.</param>
    /// <returns>True if the output file needs seeding with a copy of the input.</returns>
    bool NeedsSeededOutput(EngineRuntimeInfo runtime);

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
