// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
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
    /// Gets how many history entries are kept. Enough to cover a bulk run over a season
    /// and still be there the next morning, not so many that the config file balloons.
    /// </summary>
    public static int MaxHistoryEntries => 300;

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
    /// Gets or sets what happens when the engine finishes but isn't confident about the
    /// result. Writing the doubtful result to a sidecar is the default: it never touches
    /// a subtitle that was already fine, and it still leaves the result there to look at.
    /// </summary>
    public LowConfidenceAction LowConfidenceAction { get; set; } = LowConfidenceAction.Sidecar;

    /// <summary>
    /// Gets or sets how far LAPSE's answer has to stand out from the alternatives before
    /// it counts as confident, in standard deviations. This is passed straight to the
    /// engine as --confidence, and the default is the engine's own internal default
    /// (sure_sigma in its main.cpp), not a number of the plugin's invention.
    /// </summary>
    public double ConfidenceSigma { get; set; } = LapseEngine.DefaultConfidenceSigma;

    /// <summary>
    /// Gets or sets the old 0-100 confidence percentage. Kept only so an upgrade can tell
    /// a config written before <see cref="ConfidenceSigma"/> existed, see
    /// <see cref="MigrateLegacySettings"/>. The two are on completely different scales, so
    /// the old value is dropped rather than converted.
    /// </summary>
    public int SyncConfidenceThreshold { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether the daily task keeps every installed
    /// engine up to date. Engines that aren't installed are left alone either way.
    /// </summary>
    public bool AutoUpdateEngines { get; set; } = true;

    /// <summary>
    /// Gets or sets the Google Cloud Translation API key.
    /// </summary>
    public string? GoogleTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the DeepL API key. Free tier keys end in ":fx" and go to a different
    /// host, which the provider works out from the key itself.
    /// </summary>
    public string? DeepLApiKey { get; set; }

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
    /// Gets or sets the base URL of a self hosted LibreTranslate, e.g. http://libretranslate:5000.
    /// </summary>
    public string? LibreTranslateBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the LibreTranslate API key. Only needed when that instance asks for one.
    /// </summary>
    public string? LibreTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets which provider a translation job uses when it doesn't name one.
    /// MyMemory needs no setting up at all, so it's the one that works on a fresh install.
    /// </summary>
    public TranslationProvider DefaultTranslationProvider { get; set; } = TranslationProvider.MyMemory;

    /// <summary>
    /// Gets or sets how subtitles are restyled during playback.
    /// </summary>
    public SubtitleAppearance SubtitleAppearance { get; set; } = new();

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
    /// Gets the recent per-file history, newest last, so a sync can be undone. Capped at
    /// <see cref="MaxHistoryEntries"/> - this lives in the plugin's XML config, which is
    /// read and rewritten whole, so it must not be allowed to grow with the library.
    /// </summary>
    public List<SyncHistoryEntry> History { get; } = new();

    /// <summary>
    /// Gets the ids of items and folders that are marked as skip. An item counts
    /// as skipped if its own id is here, or any of its parent folders' ids are.
    /// </summary>
    public List<Guid> SkippedItemIds { get; } = new();

    /// <summary>
    /// Gets the ignore list: films, series and folders that no automatic or bulk run may
    /// touch. Separate from the skip list on purpose - skip is a per-item "not now" you
    /// set while working through the item list, ignore is a standing "never" that also
    /// covers everything underneath a series or a folder.
    /// </summary>
    public List<IgnoreRule> IgnoreRules { get; } = new();

    /// <summary>
    /// Gets or sets where a subtitle-to-subtitle result is written by default.
    /// </summary>
    public SubToSubPlacement SubToSubPlacement { get; set; } = SubToSubPlacement.ReferenceFolder;

    /// <summary>
    /// Gets or sets the folder used when <see cref="SubToSubPlacement"/> is
    /// <see cref="SubToSubPlacement.CustomFolder"/>.
    /// </summary>
    public string? SubToSubCustomFolder { get; set; }

    /// <summary>
    /// Gets or sets the format conversions produce when nothing asks for a specific one.
    /// srt by default: it's the format everything reads, and the one whose timestamps are
    /// simplest to edit by hand afterwards.
    /// </summary>
    public string ConversionFormat { get; set; } = "srt";

    /// <summary>
    /// Gets or sets a value indicating whether converting deletes the file it read.
    /// Off by default, so a conversion adds a file and takes nothing away - if the result
    /// is wrong, the original is still sitting there.
    /// </summary>
    public bool ConversionReplaceOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle that had to be converted before
    /// an engine could read it then gets synced, rather than the conversion being the
    /// whole job. On by default: converting was only ever a means to syncing.
    /// </summary>
    public bool ConversionSyncAfter { get; set; } = true;

    /// <summary>
    /// Gets or sets who besides administrators may sync, shift, convert and translate the
    /// subtitles of an item they can see. Admin only until an admin opens it up.
    /// </summary>
    public SubtitleAccessMode SubtitleAccess { get; set; } = SubtitleAccessMode.AdminsOnly;

    /// <summary>
    /// Gets or sets the users allowed to work on subtitles when
    /// <see cref="SubtitleAccess"/> is <see cref="SubtitleAccessMode.SelectedUsers"/>,
    /// as Jellyfin user ids.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "The XML config serializer needs to be able to assign the list when it loads the file.")]
    public List<string> SubtitleAccessUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether a sync on an item with no subtitle at all
    /// may go and fetch one from OpenSubtitles first. Experimental.
    /// </summary>
    public bool OpenSubtitlesEnabled { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles API key, sent as the Api-Key header.
    /// </summary>
    public string? OpenSubtitlesApiKey { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles account name. Their API hands out search results to
    /// an API key alone, but a download needs a token that only a login can produce, so
    /// without these two the fetch will find a subtitle and then fail to get it.
    /// </summary>
    public string? OpenSubtitlesUsername { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles account password.
    /// </summary>
    public string? OpenSubtitlesPassword { get; set; }

    /// <summary>
    /// Gets or sets the language to fetch subtitles in, as a two letter code.
    /// </summary>
    public string OpenSubtitlesLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets a value indicating whether the Radarr/Sonarr webhook endpoint accepts
    /// requests. Experimental, and off until someone turns it on.
    /// </summary>
    public bool ArrWebhookEnabled { get; set; }

    /// <summary>
    /// Gets or sets the shared secret the webhook URL carries. Radarr and Sonarr can't
    /// send a Jellyfin API key, so the endpoint is anonymous and this is what stands
    /// between it and anyone who can reach the server.
    /// </summary>
    public string? ArrWebhookToken { get; set; }

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
    /// Says whether an id or a file path is on the ignore list. Ids are matched against
    /// the item and every one of its ancestors by the caller; paths match a file that
    /// sits under the ignored folder, so ignoring a series folder covers every episode in
    /// it without needing an entry each.
    /// </summary>
    /// <param name="ids">The item's own id and its ancestors' ids.</param>
    /// <param name="path">The item's file path, if it has one.</param>
    /// <returns>True if anything on the ignore list covers this item.</returns>
    public bool IsIgnored(IEnumerable<Guid> ids, string? path)
    {
        if (IgnoreRules.Count == 0)
        {
            return false;
        }

        var idSet = new HashSet<Guid>(ids);

        foreach (var rule in IgnoreRules)
        {
            if (rule.ItemId.HasValue && idSet.Contains(rule.ItemId.Value))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rule.Path) && CoversPath(rule.Path, path))
            {
                return true;
            }
        }

        return false;
    }

    // A rule matches the file itself or anything under it, so a folder covers its whole
    // tree. Comparing on a trailing separator stops "/media/Movies" from also swallowing
    // "/media/Movies Extra".
    private static bool CoversPath(string rulePath, string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        var rule = rulePath.TrimEnd('/', '\\');

        if (itemPath.Equals(rule, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return itemPath.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase)
            || itemPath.StartsWith(rule + "\\", StringComparison.OrdinalIgnoreCase);
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

        // Config written before SubtitleAppearance existed deserializes it as null, and
        // every read of it assumes there's an object there.
        if (SubtitleAppearance is null)
        {
            SubtitleAppearance = new SubtitleAppearance();
        }

        SyncConfidenceThreshold = Math.Clamp(SyncConfidenceThreshold, 0, 100);

        // A config from before the threshold moved onto the engine's own scale
        // deserializes this as 0, which would tell LAPSE that nothing is ever confident.
        // The engine rejects anything at or below zero anyway, so snap it back to what
        // the engine itself ships with.
        if (ConfidenceSigma <= 0)
        {
            ConfidenceSigma = LapseEngine.DefaultConfidenceSigma;
        }

        if (string.IsNullOrWhiteSpace(OpenSubtitlesLanguage))
        {
            OpenSubtitlesLanguage = "en";
        }

        ClearBadAlassEncodings();
    }

    // An earlier version shipped "auto" as the default for alass's two encoding arguments,
    // taken from the "default: auto" line in its help text. The 2.0.0 binary hands that
    // string to encoding_rs and panics with "auto is not a known encoding label", so every
    // alass sync failed. The descriptor no longer offers it, but anyone who pressed Save
    // while it did has the value written into their config, where it would keep breaking
    // them. Nothing is lost by dropping it: blank is what makes alass detect the encoding,
    // which is what "auto" was meant to say in the first place.
    private void ClearBadAlassEncodings()
    {
        var alass = Engines.FirstOrDefault(e => string.Equals(e.EngineId, "alass", StringComparison.OrdinalIgnoreCase));
        if (alass is null)
        {
            return;
        }

        foreach (var parameter in alass.Parameters)
        {
            var isEncoding = string.Equals(parameter.Key, "encodingRef", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parameter.Key, "encodingInc", StringComparison.OrdinalIgnoreCase);

            if (isEncoding && string.Equals(parameter.Value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Value = null;
            }
        }
    }
}
