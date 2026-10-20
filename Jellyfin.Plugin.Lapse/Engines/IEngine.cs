// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Data;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// One sync engine. They all take arguments and print results differently, so each gets its own class.
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
    IReadOnlyList<string> BuildArguments(EngineRunOptions options);

    /// <summary>
    /// Says whether the runner has to copy the input to the output path first (engines that
    /// edit in place). Others get no file, so "did it write anything" stays meaningful.
    /// </summary>
    bool NeedsSeededOutput(EngineRuntimeInfo runtime);

    /// <summary>
    /// Reads whatever the engine printed and works out what happened.
    /// </summary>
    SyncResult ParseResult(string stdout, string stderr, int exitCode, SyncMode requestedMode, int requestedPenalty);

    /// <summary>
    /// Gets environment variables the engine needs set from its parameters. Most need none.
    /// </summary>
    IReadOnlyDictionary<string, string> GetEnvironment(EngineParameterValues values) => new Dictionary<string, string>();
}
