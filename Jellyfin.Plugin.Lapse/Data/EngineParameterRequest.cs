// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/Engines/{id}/Parameter, sets one parameter (e.g. turning on force from the low confidence prompt).
/// </summary>
public class EngineParameterRequest
{
    /// <summary>
    /// Gets or sets the parameter key, matching EngineParameter.Key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value to save, as text. Booleans are "true"/"false".
    /// </summary>
    public string? Value { get; set; }
}
