// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

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
    /// Gets or sets a value indicating whether the engine is still experimental, which
    /// the dashboard says next to its name.
    /// </summary>
    public bool Experimental { get; set; }

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

    /// <summary>
    /// Gets or sets the release tag installed, or null when the binary didn't come from
    /// the plugin (a hand built one behind a path override, say).
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets the newest release tag published, or null if GitHub couldn't be asked.
    /// </summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there's a newer release to install.
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the daily task may update this engine.
    /// </summary>
    public bool AutoUpdate { get; set; }

    /// <summary>
    /// Gets or sets the version the installed binary reports about itself, when it can.
    /// </summary>
    public string? ReportedVersion { get; set; }

    /// <summary>
    /// Gets or sets the flags the installed binary said it understands.
    /// </summary>
    public IReadOnlyList<string>? DiscoveredFlags { get; set; }

    /// <summary>
    /// Gets or sets how the flags were discovered: "capabilities" when the binary answered
    /// --capabilities, "usage" when they were read out of its usage text, null when the
    /// binary couldn't be asked at all.
    /// </summary>
    public string? CapabilitySource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the installed binary takes --output.
    /// </summary>
    public bool SupportsOutputFlag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the installed binary takes --no-backup.
    /// </summary>
    public bool SupportsNoBackupFlag { get; set; }

    /// <summary>
    /// Gets or sets the reason there's no download for this machine, when there isn't one.
    /// This is the "here's what to do instead" text the dashboard shows on the card.
    /// </summary>
    public string? NoDownloadReason { get; set; }

    /// <summary>
    /// Gets or sets a page explaining how to build this engine from source.
    /// </summary>
    public string? BuildGuideUrl { get; set; }
}
