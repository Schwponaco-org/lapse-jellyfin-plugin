// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One alignment mode an engine offers, as the dashboard sees it.
/// </summary>
public class EngineModeInfo
{
    /// <summary>
    /// Gets or sets the mode name, matching the SyncMode enum.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what the dropdown shows.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one line explanation.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// One of an engine's advanced parameters, with its definition and its current value, so
/// the dashboard can draw the right control without knowing anything about the engine.
/// </summary>
public class EngineParameterInfo
{
    /// <summary>
    /// Gets or sets the parameter key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the label.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one line explanation.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command line flag this drives, shown next to the label.
    /// </summary>
    public string? Flag { get; set; }

    /// <summary>
    /// Gets or sets the control to draw: Boolean, Number, Text or Select.
    /// </summary>
    public string Kind { get; set; } = "Text";

    /// <summary>
    /// Gets or sets the engine's own default, as text.
    /// </summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what's currently set, which is the default until someone changes it.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lowest accepted number.
    /// </summary>
    public double? Minimum { get; set; }

    /// <summary>
    /// Gets or sets the highest accepted number.
    /// </summary>
    public double? Maximum { get; set; }

    /// <summary>
    /// Gets or sets the step for number inputs.
    /// </summary>
    public double Step { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether leaving it blank means "don't pass the flag".
    /// </summary>
    public bool BlankMeansUnset { get; set; }

    /// <summary>
    /// Gets the choices for a select parameter, as value/label pairs.
    /// </summary>
    public List<EngineModeInfo> Options { get; } = new();
}

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
    /// Gets or sets the badge next to the name: "Recommended", "Supported" or
    /// "Experimental".
    /// </summary>
    public string Tier { get; set; } = "Supported";

    /// <summary>
    /// Gets or sets a page backing the badge up, linked right under it.
    /// </summary>
    public string? WhyUrl { get; set; }

    /// <summary>
    /// Gets or sets the text of the <see cref="WhyUrl"/> link.
    /// </summary>
    public string? WhyLabel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the engine can decide the alignment shape
    /// for itself.
    /// </summary>
    public bool SupportsAuto { get; set; }

    /// <summary>
    /// Gets or sets the mode a plain Sync press uses with this engine.
    /// </summary>
    public string DefaultMode { get; set; } = "Standard";

    /// <summary>
    /// Gets the modes this engine offers, in the order to show them.
    /// </summary>
    public List<EngineModeInfo> Modes { get; } = new();

    /// <summary>
    /// Gets this engine's advanced parameters, with whatever is currently set for each.
    /// </summary>
    public List<EngineParameterInfo> Parameters { get; } = new();

    /// <summary>
    /// Gets or sets a read-only line shown at the top of the Advanced section.
    /// </summary>
    public string? AdvancedNote { get; set; }

    /// <summary>
    /// Gets or sets a line about running this engine outside Jellyfin.
    /// </summary>
    public string? DeploymentNote { get; set; }

    /// <summary>
    /// Gets or sets the subtitle formats this engine reads, as extensions with a leading
    /// dot. Comes from the installed binary when it can say, otherwise from what the
    /// plugin knows about the project.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Built in one go from the engine descriptor or the runtime probe, and only ever serialized out to the dashboard.")]
    public IReadOnlyList<string> SubtitleExtensions { get; set; } = System.Array.Empty<string>();

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
    /// Gets or sets the penalty the engine itself ships with, which is what the settings
    /// form quotes as the standard value. Not the same as <see cref="Penalty"/>, which is
    /// whatever is configured right now.
    /// </summary>
    public int DefaultPenalty { get; set; }

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
    /// Gets or sets a value indicating whether the engine is installed but neither the
    /// recorded release tag nor the binary itself could say which version it is.
    /// </summary>
    public bool VersionUnknown { get; set; }

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
