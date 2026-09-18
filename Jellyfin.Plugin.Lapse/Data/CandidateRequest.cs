// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// Which of the waiting answers to keep, or which item's answers to throw away.
/// </summary>
public class CandidateRequest
{
    /// <summary>
    /// Gets or sets the item the answers belong to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the candidate file to keep. Left empty when keeping whatever is
    /// playing, which the server works out from the session instead.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets which subtitle's answers to discard, or null for all of the item's.
    /// </summary>
    public string? OriginalPath { get; set; }
}
