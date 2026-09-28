// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Where the scheduled subtitle extraction is at, for the dashboard.
/// </summary>
public class ExtractionStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a run is going now.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets the current or most recent run since the server started, or null.
    /// </summary>
    public ExtractionRunSummary? LastRun { get; set; }
}
