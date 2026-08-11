// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// What a given engine can actually do. The dashboard uses this to grey out modes an
/// engine doesn't support instead of letting you pick something that would just fail.
/// </summary>
public class EngineCapabilities
{
    /// <summary>
    /// Gets or sets a value indicating whether the engine can do a plain constant shift.
    /// Every engine we ship can do this, it's the baseline.
    /// </summary>
    public bool SupportsStandard { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the engine decides for itself what shape
    /// the problem is and handles it, rather than being told which alignment to run. Only
    /// LAPSE does this.
    /// </summary>
    public bool SupportsAuto { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can fit a slope and intercept
    /// across the whole file. Only LAPSE does this one.
    /// </summary>
    public bool SupportsOls { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can break the subtitle into
    /// sections with their own timing.
    /// </summary>
    public bool SupportsSplit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether split mode takes a penalty number. ffsubsync
    /// has no split at all, and some engines could have split without a tunable penalty,
    /// so this is tracked separately from <see cref="SupportsSplit"/>.
    /// </summary>
    public bool SupportsPenalty { get; set; }

    /// <summary>
    /// Gets or sets the penalty this engine works best with. The scales are completely
    /// different between engines (LAPSE uses about 6, alass uses 0-1000 with 7 as its
    /// own default), so this travels with the engine rather than being one global setting.
    /// </summary>
    public int DefaultPenalty { get; set; }

    /// <summary>
    /// Gets or sets the lowest penalty the engine accepts.
    /// </summary>
    public int MinPenalty { get; set; }

    /// <summary>
    /// Gets or sets the highest penalty the engine accepts.
    /// </summary>
    public int MaxPenalty { get; set; }
}
