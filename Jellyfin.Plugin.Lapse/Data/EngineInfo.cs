// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One engine as the dashboard sees it: who it is, whether it's usable, and what it can do.
/// </summary>
public class EngineInfo
{
    /// <summary>
    /// Gets or sets the engine id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name to show.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one line description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project's home page.
    /// </summary>
    public string ProjectUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the binary is on disk.
    /// </summary>
    public bool Installed { get; set; }

    /// <summary>
    /// Gets or sets where the binary is (or would be).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this engine is the current default.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there's a build for this machine.
    /// </summary>
    public bool DownloadSupported { get; set; }

    /// <summary>
    /// Gets or sets why the engine can't run, if it can't. Null means it looks fine.
    /// </summary>
    public string? RunCheckError { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can do a plain constant shift.
    /// </summary>
    public bool SupportsStandard { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can do OLS alignment.
    /// </summary>
    public bool SupportsOls { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can do split alignment.
    /// </summary>
    public bool SupportsSplit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether split mode takes a penalty number.
    /// </summary>
    public bool SupportsPenalty { get; set; }

    /// <summary>
    /// Gets or sets the penalty currently in effect for this engine.
    /// </summary>
    public int Penalty { get; set; }

    /// <summary>
    /// Gets or sets the lowest penalty this engine accepts.
    /// </summary>
    public int MinPenalty { get; set; }

    /// <summary>
    /// Gets or sets the highest penalty this engine accepts.
    /// </summary>
    public int MaxPenalty { get; set; }

    /// <summary>
    /// Gets or sets the configured path override, if there is one.
    /// </summary>
    public string? PathOverride { get; set; }
}
