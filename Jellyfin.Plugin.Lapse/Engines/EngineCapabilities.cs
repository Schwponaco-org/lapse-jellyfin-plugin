// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// What an engine can do. The dashboard greys out the rest.
/// </summary>
public class EngineCapabilities
{
    /// <summary>
    /// Gets or sets a value indicating whether the engine can do a plain shift (all of them can).
    /// </summary>
    public bool SupportsStandard { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the engine works out the alignment by itself (LAPSE only).
    /// </summary>
    public bool SupportsAuto { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can fit a slope across the file (LAPSE only).
    /// </summary>
    public bool SupportsOls { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can split the file into differently timed sections.
    /// </summary>
    public bool SupportsSplit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether split takes a penalty number (separate from <see cref="SupportsSplit"/>).
    /// </summary>
    public bool SupportsPenalty { get; set; }

    /// <summary>
    /// Gets or sets the default penalty. Scales differ wildly (LAPSE ~6, alass 0-1000 with 7), so it's per engine.
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

    /// <summary>
    /// Gets or sets the extensions it reads and writes. The installed binary's own answer wins, see <see cref="EngineFormats"/>.
    /// </summary>
    public IReadOnlyList<string> SubtitleExtensions { get; set; } = EngineFormats.Ffsubsync;
}
