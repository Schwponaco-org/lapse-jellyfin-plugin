// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// LAPSE settings, saved to disk as XML by Jellyfin's usual plugin config machinery.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DefaultPenalty = 6;
        DefaultEngineId = "lapse";
    }

    /// <summary>
    /// Gets or sets which engine to use when a sync doesn't ask for a specific one.
    /// </summary>
    public string DefaultEngineId { get; set; }

    /// <summary>
    /// Gets the per-engine settings.
    /// </summary>
    public List<EngineSettings> Engines { get; } = new();

    /// <summary>
    /// Gets or sets the old single default penalty. Kept so upgrading from a version that
    /// only had one engine doesn't throw away the value, see <see cref="MigrateLegacySettings"/>.
    /// </summary>
    public int DefaultPenalty { get; set; }

    /// <summary>
    /// Gets or sets the old single binary path override. Same deal as DefaultPenalty.
    /// </summary>
    public string? EngineBinaryPathOverride { get; set; }

    /// <summary>
    /// Gets the sync history for every movie we've ever synced.
    /// </summary>
    public List<MovieSyncRecord> MovieRecords { get; } = new();

    /// <summary>
    /// Gets the ids of movies and folders that are marked as skip. A movie counts
    /// as skipped if its own id is here, or any of its parent folders' ids are.
    /// </summary>
    public List<Guid> SkippedItemIds { get; } = new();

    /// <summary>
    /// Finds the settings for one engine, creating an entry if there isn't one yet.
    /// </summary>
    /// <param name="engineId">The engine id.</param>
    /// <returns>That engine's settings.</returns>
    public EngineSettings GetEngineSettings(string engineId)
    {
        var existing = Engines.FirstOrDefault(e => string.Equals(e.EngineId, engineId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new EngineSettings { EngineId = engineId };
        Engines.Add(created);
        return created;
    }

    /// <summary>
    /// Moves settings from the old single-engine days onto the LAPSE engine entry, so
    /// upgrading doesn't silently lose a configured binary path or penalty. Only does
    /// anything the first time, since it clears the old fields as it goes.
    /// </summary>
    public void MigrateLegacySettings()
    {
        if (!string.IsNullOrWhiteSpace(EngineBinaryPathOverride))
        {
            var lapse = GetEngineSettings("lapse");
            lapse.PathOverride ??= EngineBinaryPathOverride;
            EngineBinaryPathOverride = null;
        }

        // 6 is the old default, so only carry it over if it was actually changed
        if (DefaultPenalty > 0 && DefaultPenalty != 6)
        {
            var lapse = GetEngineSettings("lapse");
            lapse.Penalty ??= DefaultPenalty;
            DefaultPenalty = 6;
        }
    }
}
