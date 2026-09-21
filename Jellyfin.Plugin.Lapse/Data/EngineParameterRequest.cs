// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Body for POST /Lapse/Engines/{id}/Parameter. Sets one advanced parameter on one engine,
/// for callers that only want to flip a single switch rather than posting the whole
/// settings page - the low-confidence prompts that offer to turn on "force" in place.
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
