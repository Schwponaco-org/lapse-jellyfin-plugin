// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Status of one item sitting in the bulk sync queue.
/// </summary>
public enum QueueItemStatus
{
    /// <summary>
    /// Waiting for its turn.
    /// </summary>
    Queued,

    /// <summary>
    /// Being synced right now.
    /// </summary>
    Running,

    /// <summary>
    /// Finished, one way or another (see the movie's own status for success/failure).
    /// </summary>
    Done,

    /// <summary>
    /// The sync itself threw an error the queue couldn't recover from.
    /// </summary>
    Failed
}
