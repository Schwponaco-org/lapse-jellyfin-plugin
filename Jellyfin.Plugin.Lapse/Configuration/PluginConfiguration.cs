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
    /// Gets the per-library settings. A library with no entry here counts as enabled,
    /// so upgrading doesn't silently stop syncing anything that used to get synced.
    /// </summary>
    public List<LibraryConfig> Libraries { get; } = new();

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
    /// Gets or sets where synced subtitles get written, and whether the original is kept.
    /// </summary>
    public OutputMode OutputMode { get; set; } = OutputMode.OverwriteWithBackup;

    /// <summary>
    /// Gets or sets what gets inserted before the extension in the sidecar output modes,
    /// e.g. ".shifted" turns Movie.en.srt into Movie.en.shifted.srt.
    /// </summary>
    public string SidecarSuffix { get; set; } = ".shifted";

    /// <summary>
    /// Gets or sets the Google Cloud Translation API key.
    /// </summary>
    public string? GoogleTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the base URL of a self hosted Lingarr, e.g. http://lingarr:9876.
    /// </summary>
    public string? LingarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lingarr API key, sent as X-Api-Key. Only needed when that Lingarr
    /// has authentication turned on.
    /// </summary>
    public string? LingarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets which provider a translation job uses when it doesn't name one.
    /// </summary>
    public TranslationProvider DefaultTranslationProvider { get; set; } = TranslationProvider.Google;

    /// <summary>
    /// Gets or sets the default confidence threshold (0-100) for translation jobs.
    /// </summary>
    public int TranslationConfidenceThreshold { get; set; } = 70;

    /// <summary>
    /// Gets or sets a value indicating whether translated files get a comment block at the
    /// top saying who translated them, when, and how confident it was.
    /// </summary>
    public bool TranslationIncludeMetadataHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether lines that score below the threshold keep
    /// their original text (true) or get flagged in the output but still translated (false).
    /// </summary>
    public bool TranslationKeepLowConfidenceOriginal { get; set; }

    /// <summary>
    /// Gets the sync history for every item we've ever synced.
    /// </summary>
    public List<MovieSyncRecord> MovieRecords { get; } = new();

    /// <summary>
    /// Gets the ids of items and folders that are marked as skip. An item counts
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
    /// Finds the settings for one library, creating an entry if there isn't one yet.
    /// </summary>
    /// <param name="libraryId">The CollectionFolder id.</param>
    /// <returns>That library's settings.</returns>
    public LibraryConfig GetLibraryConfig(Guid libraryId)
    {
        var existing = Libraries.FirstOrDefault(l => l.LibraryId == libraryId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new LibraryConfig { LibraryId = libraryId };
        Libraries.Add(created);
        return created;
    }

    /// <summary>
    /// Says whether a library is eligible for sync. Libraries the user has never touched
    /// in the dashboard have no entry at all, and those count as enabled.
    /// </summary>
    /// <param name="libraryId">The CollectionFolder id.</param>
    /// <returns>True if items in this library may be synced.</returns>
    public bool IsLibraryEnabled(Guid libraryId)
    {
        var existing = Libraries.FirstOrDefault(l => l.LibraryId == libraryId);
        return existing?.Enabled ?? true;
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

        if (string.IsNullOrWhiteSpace(SidecarSuffix))
        {
            SidecarSuffix = ".shifted";
        }
    }
}
