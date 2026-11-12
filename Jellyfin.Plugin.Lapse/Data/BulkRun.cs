// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// What one finished bulk run came to.
/// </summary>
public class BulkRun
{
    /// <summary>
    /// Gets or sets what the run was, e.g. "Scheduled sync" or a series name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it started.
    /// </summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the last item finished.
    /// </summary>
    public DateTime FinishedUtc { get; set; }

    /// <summary>
    /// Gets or sets how many items synced.
    /// </summary>
    public int Synced { get; set; }

    /// <summary>
    /// Gets or sets how many items the engine wasn't sure about.
    /// </summary>
    public int Unsure { get; set; }

    /// <summary>
    /// Gets or sets how many items failed.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets how many items had nothing to do, were already synced or got stopped.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether someone pressed Stop.
    /// </summary>
    public bool Stopped { get; set; }
}
