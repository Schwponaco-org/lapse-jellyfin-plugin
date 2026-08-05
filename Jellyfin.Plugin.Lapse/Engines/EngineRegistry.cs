// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// The engines the plugin knows about, and the rules for picking one.
/// </summary>
public class EngineRegistry
{
    private readonly List<IEngine> _engines;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineRegistry"/> class.
    /// </summary>
    public EngineRegistry()
    {
        _engines = new List<IEngine>
        {
            new LapseEngine(),
            new AlassEngine(),
            new FfsubsyncEngine()
        };
    }

    /// <summary>
    /// Gets the id used when nothing has been configured yet.
    /// </summary>
    public static string FallbackEngineId => "lapse";

    /// <summary>
    /// Gets every engine, in the order they should show up in the dashboard.
    /// </summary>
    public IReadOnlyList<IEngine> All => _engines;

    /// <summary>
    /// Finds an engine by id.
    /// </summary>
    /// <param name="id">The engine id.</param>
    /// <returns>The engine, or null if there isn't one with that id.</returns>
    public IEngine? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _engines.FirstOrDefault(e => string.Equals(e.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the engine a sync should use when the request didn't name one. Falls back to
    /// LAPSE if the configured default has gone missing somehow.
    /// </summary>
    /// <returns>The default engine.</returns>
    public IEngine GetDefault()
    {
        var configured = Plugin.Instance?.Configuration.DefaultEngineId;
        return Find(configured) ?? Find(FallbackEngineId) ?? _engines[0];
    }

    /// <summary>
    /// Gets the engine for a request, falling back to the configured default when the
    /// request didn't specify one.
    /// </summary>
    /// <param name="engineId">The requested engine id, possibly null.</param>
    /// <returns>The engine to run.</returns>
    public IEngine Resolve(string? engineId)
    {
        return string.IsNullOrWhiteSpace(engineId) ? GetDefault() : Find(engineId) ?? GetDefault();
    }
}
