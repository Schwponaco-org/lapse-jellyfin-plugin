// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One movie waiting in (or moving through) the bulk sync queue.
/// </summary>
public class QueueItem
{
    /// <summary>
    /// Gets or sets the movie's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the movie name, just so the dashboard has something to show without
    /// looking it up again.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the queue status.
    /// </summary>
    public QueueItemStatus Status { get; set; } = QueueItemStatus.Queued;
}
