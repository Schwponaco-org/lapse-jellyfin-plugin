// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// One saved value for one of an engine's advanced parameters. A list of key/value pairs
/// rather than a property per switch, because the switches belong to the engines and the
/// plugin shouldn't need a config migration every time one of them grows a flag.
/// </summary>
public class EngineParameterSetting
{
    /// <summary>
    /// Gets or sets the parameter key, matching EngineParameter.Key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the saved value, as text. Booleans are "true"/"false", numbers are
    /// invariant culture. An empty string means "leave the flag off".
    /// </summary>
    public string? Value { get; set; }
}

/// <summary>
/// Per-engine settings. Stored as a list rather than a dictionary because Jellyfin's XML
/// config serializer handles lists cleanly and dictionaries badly.
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Gets or sets which engine this is for.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom path to the binary. When empty the plugin uses whatever it
    /// installed into its own engines folder.
    /// </summary>
    public string? PathOverride { get; set; }

    /// <summary>
    /// Gets or sets the penalty to use for split mode with this engine. Null means use the
    /// engine's own default, which matters because the scales differ wildly between them.
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets the mode a plain "Sync" press uses with this engine - the button in
    /// the three-dot context menu and the one on each row of the item list. Null means
    /// the engine's own first offered mode.
    /// </summary>
    public string? DefaultMode { get; set; }

    /// <summary>
    /// Gets the saved values for this engine's advanced parameters.
    /// </summary>
    public List<EngineParameterSetting> Parameters { get; } = new();

    /// <summary>
    /// Gets or sets the release tag of the copy the plugin installed, e.g. "v1.0.7". Null
    /// when the engine was never installed through the plugin (a hand built binary behind
    /// a path override, say), in which case there's nothing to compare a release against.
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets when the plugin last asked GitHub what the newest release was. Used to
    /// keep the dashboard from hammering the API on every page load.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Gets or sets the newest release tag seen at <see cref="LastUpdateCheckUtc"/>.
    /// </summary>
    public string? LatestKnownVersion { get; set; }

    /// <summary>
    /// Reads the advanced parameters into the shape an engine wants them.
    /// </summary>
    /// <returns>Key to value, with nothing filled in for parameters that were never saved.</returns>
    public Dictionary<string, string?> GetParameterMap()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in Parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Key))
            {
                map[parameter.Key] = parameter.Value;
            }
        }

        return map;
    }

    /// <summary>
    /// Replaces the saved advanced parameters with what the settings form sent.
    /// </summary>
    /// <param name="values">Key to value.</param>
    public void SetParameters(IEnumerable<KeyValuePair<string, string?>> values)
    {
        Parameters.Clear();

        foreach (var pair in values.Where(p => !string.IsNullOrWhiteSpace(p.Key)))
        {
            Parameters.Add(new EngineParameterSetting { Key = pair.Key, Value = pair.Value });
        }
    }
}
