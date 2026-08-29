// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Snapshot of the bulk sync queue, sent to the dashboard so it can show a progress bar.
/// </summary>
public class QueueSnapshot
{
    /// <summary>
    /// Gets or sets a value indicating whether a bulk sync job is currently running.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets how many items were in this job in total.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets how many items are done (synced or failed, doesn't matter which).
    /// </summary>
    public int Completed { get; set; }

    /// <summary>
    /// Gets or sets the name of the item currently being synced, if any.
    /// </summary>
    public string? CurrentItemName { get; set; }

    /// <summary>
    /// Gets or sets what this job is, e.g. "The Expanse" for a whole-series sync. Used to
    /// say what the progress is about rather than just counting.
    /// </summary>
    public string? JobName { get; set; }

    /// <summary>
    /// Gets or sets what the items in this job are called, singular, e.g. "episode".
    /// </summary>
    public string? UnitName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether someone asked for this job to stop and the
    /// item still running hasn't finished winding down yet.
    /// </summary>
    public bool Cancelling { get; set; }

    /// <summary>
    /// Gets or sets the full list of items in the current job, in queue order.
    /// </summary>
    public List<QueueItem> Items { get; init; } = new();
}
