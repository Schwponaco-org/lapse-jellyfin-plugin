// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// A saved value for one advanced engine parameter. Key/value so a new engine flag
/// doesn't need a config migration.
/// </summary>
public class EngineParameterSetting
{
    /// <summary>
    /// Gets or sets the parameter key, matching EngineParameter.Key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value as text ("true"/"false", invariant numbers). Empty leaves the flag off.
    /// </summary>
    public string? Value { get; set; }
}

/// <summary>
/// Per-engine settings. A list entry rather than a dictionary since XmlSerializer can't do dictionaries.
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Gets or sets which engine this is for.
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom binary path. Empty uses the one we installed.
    /// </summary>
    public string? PathOverride { get; set; }

    /// <summary>
    /// Gets or sets the split mode penalty. Null uses the engine default (the scales differ a lot between engines).
    /// </summary>
    public int? Penalty { get; set; }

    /// <summary>
    /// Gets or sets the mode a plain Sync press uses. Null means the engine's first mode.
    /// </summary>
    public string? DefaultMode { get; set; }

    /// <summary>
    /// Gets the saved values for this engine's advanced parameters.
    /// </summary>
    public List<EngineParameterSetting> Parameters { get; } = new();

    /// <summary>
    /// Gets or sets the release tag we installed, e.g. "v1.0.7". Null for hand installed binaries.
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets when we last asked GitHub for the newest release, so we don't on every page load.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Gets or sets the newest release tag seen at <see cref="LastUpdateCheckUtc"/>.
    /// </summary>
    public string? LatestKnownVersion { get; set; }

    /// <summary>
    /// Reads the advanced parameters into the shape an engine wants them.
    /// </summary>
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
    public void SetParameters(IEnumerable<KeyValuePair<string, string?>> values)
    {
        Parameters.Clear();

        foreach (var pair in values.Where(p => !string.IsNullOrWhiteSpace(p.Key)))
        {
            Parameters.Add(new EngineParameterSetting { Key = pair.Key, Value = pair.Value });
        }
    }
}
