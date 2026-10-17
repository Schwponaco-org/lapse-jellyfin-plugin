// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Lapse.Configuration;

/// <summary>
/// LAPSE settings. Jellyfin saves this to disk as XML, so property names can't change
/// without breaking existing configs.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // Lookup for MovieRecords so a bulk run doesn't scan the whole list for every item.
    // Not serialized. Rebuilt when the list length changes (records are only ever added).
    private Dictionary<Guid, MovieSyncRecord>? _recordIndex;
    private int _indexedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DefaultPenalty = 6;
        DefaultEngineId = "lapse";
    }

    /// <summary>
    /// Gets how many history entries we keep. The whole config is rewritten on every save so this has to stay small.
    /// </summary>
    public static int MaxHistoryEntries => 300;

    /// <summary>
    /// Gets or sets the engine used when a sync doesn't ask for a specific one.
    /// </summary>
    public string DefaultEngineId { get; set; }

    /// <summary>
    /// Gets the per-engine settings.
    /// </summary>
    public List<EngineSettings> Engines { get; } = new();

    /// <summary>
    /// Gets the per-library settings. No entry means enabled.
    /// </summary>
    public List<LibraryConfig> Libraries { get; } = new();

    /// <summary>
    /// Gets or sets the penalty from the single engine days. Only read by <see cref="MigrateLegacySettings"/>.
    /// </summary>
    public int DefaultPenalty { get; set; }

    /// <summary>
    /// Gets or sets the binary path from the single engine days. Only read by <see cref="MigrateLegacySettings"/>.
    /// </summary>
    public string? EngineBinaryPathOverride { get; set; }

    /// <summary>
    /// Gets or sets where synced subtitles get written and whether the original is kept.
    /// </summary>
    public OutputMode OutputMode { get; set; } = OutputMode.SidecarOnly;

    /// <summary>
    /// Gets or sets the suffix for sidecar output, e.g. ".shifted" gives Movie.en.shifted.srt.
    /// </summary>
    public string SidecarSuffix { get; set; } = ".shifted";

    /// <summary>
    /// Gets or sets what happens when the engine isn't confident. Sidecar by default so a good subtitle never gets overwritten.
    /// </summary>
    public LowConfidenceAction LowConfidenceAction { get; set; } = LowConfidenceAction.Sidecar;

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle the engine barely moved is left alone.
    /// </summary>
    public bool SkipAlreadyInSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the shift (ms) below which <see cref="SkipAlreadyInSync"/> counts a subtitle as already right.
    /// </summary>
    public int AlreadyInSyncToleranceMs { get; set; } = 100;

    /// <summary>
    /// Gets or sets the --confidence value passed to LAPSE (standard deviations). Default matches the engine.
    /// </summary>
    public double ConfidenceSigma { get; set; } = LapseEngine.DefaultConfidenceSigma;

    /// <summary>
    /// Gets or sets the old 0-100 confidence value. Only kept so old configs still load.
    /// </summary>
    public int SyncConfidenceThreshold { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether the daily task updates installed engines.
    /// </summary>
    public bool AutoUpdateEngines { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether embedded tracks count towards an item being synced.
    /// Off by default since nothing automatic touches them, so items would sit on "partly synced" forever.
    /// </summary>
    public bool CountEmbeddedSubtitlesInStatus { get; set; }

    /// <summary>
    /// Gets or sets the Google Cloud Translation API key.
    /// </summary>
    public string? GoogleTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the DeepL API key. Free keys end in ":fx".
    /// </summary>
    public string? DeepLApiKey { get; set; }

    /// <summary>
    /// Gets or sets the Lingarr base URL, e.g. http://lingarr:9876.
    /// </summary>
    public string? LingarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lingarr API key (only if auth is on).
    /// </summary>
    public string? LingarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets the LibreTranslate base URL, e.g. http://libretranslate:5000.
    /// </summary>
    public string? LibreTranslateBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the LibreTranslate API key, if the instance wants one.
    /// </summary>
    public string? LibreTranslateApiKey { get; set; }

    /// <summary>
    /// Gets or sets the provider used when a translation job doesn't pick one.
    /// </summary>
    public TranslationProvider DefaultTranslationProvider { get; set; } = TranslationProvider.MyMemory;

    /// <summary>
    /// Gets or sets the server wide playback appearance. Users can override it.
    /// </summary>
    public SubtitleAppearance SubtitleAppearance { get; set; } = new();

    /// <summary>
    /// Gets the per-user playback appearance overrides.
    /// </summary>
    public List<UserSubtitleAppearance> UserSubtitleAppearances { get; } = new();

    /// <summary>
    /// Gets or sets the default confidence threshold (0-100) for translations.
    /// </summary>
    public int TranslationConfidenceThreshold { get; set; } = 70;

    /// <summary>
    /// Gets or sets the target language the translate dialog starts on, e.g. "da".
    /// </summary>
    public string? TranslationDefaultTargetLanguage { get; set; }

    /// <summary>
    /// Gets or sets the assumed source language, or null to let the provider detect it.
    /// </summary>
    public string? TranslationDefaultSourceLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether translated files get a comment header at the top.
    /// </summary>
    public bool TranslationIncludeMetadataHeader { get; set; } = true;

    // Readable subtitle styling. Kept server side because most clients have no font picker,
    // and styling baked into the file works everywhere.

    /// <summary>
    /// Gets or sets the font readable subtitles ask for.
    /// </summary>
    public string SubtitleFontName { get; set; } = Data.SubtitleStyle.DyslexicFontName;

    /// <summary>
    /// Gets or sets the font used for non-Latin scripts (OpenDyslexic has no glyphs for those). Empty means Arial.
    /// </summary>
    public string? SubtitleNonLatinFontName { get; set; }

    /// <summary>
    /// Gets or sets what automatic runs do about readable subtitles.
    /// </summary>
    public ReadableAutomationMode ReadableAutomation { get; set; } = ReadableAutomationMode.Off;

    /// <summary>
    /// Gets or sets the readable font size, against a 1080 tall script.
    /// </summary>
    public int SubtitleFontSize { get; set; } = 78;

    /// <summary>
    /// Gets or sets the extra letter spacing.
    /// </summary>
    public double SubtitleLetterSpacing { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether readable subtitles are bold.
    /// </summary>
    public bool SubtitleBold { get; set; }

    /// <summary>
    /// Gets or sets the outline thickness.
    /// </summary>
    public double SubtitleOutline { get; set; } = 3.5;

    /// <summary>
    /// Gets or sets the bottom margin.
    /// </summary>
    public int SubtitleMarginV { get; set; } = 70;

    /// <summary>
    /// Gets or sets a value indicating whether low confidence lines keep the original text instead of being flagged.
    /// </summary>
    public bool TranslationKeepLowConfidenceOriginal { get; set; }

    /// <summary>
    /// Gets the sync record for every item we've synced. Look one up with <see cref="FindRecord"/>.
    /// </summary>
    public List<MovieSyncRecord> MovieRecords { get; } = new();

    /// <summary>
    /// Gets the recent per-file history (newest last) so syncs can be undone. Capped at <see cref="MaxHistoryEntries"/>.
    /// </summary>
    public List<SyncHistoryEntry> History { get; } = new();

    /// <summary>
    /// Gets the item and folder ids marked as skip. Covers everything under a skipped folder too.
    /// </summary>
    public List<Guid> SkippedItemIds { get; } = new();

    /// <summary>
    /// Gets the ignore list. Unlike skip, this is a standing "never touch automatically".
    /// </summary>
    public List<IgnoreRule> IgnoreRules { get; } = new();

    /// <summary>
    /// Gets or sets where a subtitle-to-subtitle result goes by default.
    /// </summary>
    public SubToSubPlacement SubToSubPlacement { get; set; } = SubToSubPlacement.ReferenceFolder;

    /// <summary>
    /// Gets or sets the folder for <see cref="SubToSubPlacement.CustomFolder"/>.
    /// </summary>
    public string? SubToSubCustomFolder { get; set; }

    /// <summary>
    /// Gets or sets the default conversion format.
    /// </summary>
    public string ConversionFormat { get; set; } = "srt";

    /// <summary>
    /// Gets or sets a value indicating whether converting deletes the source file.
    /// </summary>
    public bool ConversionReplaceOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle converted for an engine gets synced afterwards.
    /// </summary>
    public bool ConversionSyncAfter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every subtitle is converted before syncing,
    /// even when the engine could read it as is.
    /// </summary>
    public bool ConvertBeforeSync { get; set; }

    /// <summary>
    /// Gets or sets what unattended runs (schedule, auto-sync, bulk, webhook) do to each subtitle.
    /// </summary>
    public AutomationAction AutomationAction { get; set; } = AutomationAction.Sync;

    /// <summary>
    /// Gets or sets a value indicating whether unattended runs also translate. Experimental.
    /// </summary>
    public bool AutoTranslateEnabled { get; set; }

    /// <summary>
    /// Gets or sets the language auto translation targets, e.g. "da".
    /// </summary>
    public string? AutoTranslateLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether existing translations are left alone.
    /// </summary>
    public bool AutoTranslateSkipExisting { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether unattended runs skip subtitles that are synced and untouched since.
    /// </summary>
    public bool SkipSyncedInUnattendedRuns { get; set; } = true;

    /// <summary>
    /// Gets or sets who besides admins may work on subtitles.
    /// </summary>
    public SubtitleAccessMode SubtitleAccess { get; set; } = SubtitleAccessMode.AdminsOnly;

    /// <summary>
    /// Gets or sets the user ids allowed in when access is <see cref="SubtitleAccessMode.SelectedUsers"/>.
    /// </summary>
    public List<string> SubtitleAccessUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether a sync with no subtitle may fetch one from OpenSubtitles. Experimental.
    /// </summary>
    public bool OpenSubtitlesEnabled { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles API key.
    /// </summary>
    public string? OpenSubtitlesApiKey { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles username. Downloads need a login, search doesn't.
    /// </summary>
    public string? OpenSubtitlesUsername { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles password.
    /// </summary>
    public string? OpenSubtitlesPassword { get; set; }

    /// <summary>
    /// Gets or sets the language to fetch, two letter code.
    /// </summary>
    public string OpenSubtitlesLanguage { get; set; } = "en";

    // Multi engine (experimental): when LAPSE isn't sure, run alass/ffsubsync too and keep
    // the answers side by side. Only LAPSE scores itself, so it has to be the trigger.

    /// <summary>
    /// Gets or sets a value indicating whether an unsure LAPSE result runs the other engines.
    /// </summary>
    public bool MultiEngineEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether alass is asked.
    /// </summary>
    public bool MultiEngineUseAlass { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether ffsubsync is asked.
    /// </summary>
    public bool MultiEngineUseFfsubsync { get; set; } = true;

    /// <summary>
    /// Gets or sets which LAPSE verdicts trigger the other engines.
    /// </summary>
    public MultiEngineTrigger MultiEngineTrigger { get; set; } = MultiEngineTrigger.UnsureOnly;

    /// <summary>
    /// Gets or sets the format candidate files are written in.
    /// </summary>
    public CandidateFormat MultiEngineCandidateFormat { get; set; } = CandidateFormat.MatchOriginal;

    /// <summary>
    /// Gets or sets a value indicating whether unattended runs make candidates too. Can leave a lot of files on a big library.
    /// </summary>
    public bool MultiEngineInBulk { get; set; }

    /// <summary>
    /// Gets the candidate sets waiting for someone to pick one.
    /// </summary>
    public List<SyncCandidateSet> PendingCandidates { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the Radarr/Sonarr webhook is on. Experimental.
    /// </summary>
    public bool ArrWebhookEnabled { get; set; }

    /// <summary>
    /// Gets or sets the shared secret in the webhook URL. The endpoint is anonymous so this is the only guard.
    /// </summary>
    public string? ArrWebhookToken { get; set; }

    // Scheduled extraction (experimental): write chosen embedded tracks out as files.

    /// <summary>
    /// Gets or sets a value indicating whether scheduled extraction is allowed to touch files.
    /// </summary>
    public bool ExtractEmbeddedEnabled { get; set; }

    /// <summary>
    /// Gets or sets the libraries extraction goes through. Empty means none.
    /// </summary>
    public List<Guid> ExtractLibraryIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the languages to extract (codes or names, comma or space separated). Empty means all.
    /// </summary>
    public string? ExtractLanguages { get; set; }

    /// <summary>
    /// Gets or sets which tracks to take by forced flag.
    /// </summary>
    public ExtractTrackFilter ExtractTrackFilter { get; set; } = ExtractTrackFilter.All;

    /// <summary>
    /// Gets or sets a value indicating whether a track is skipped when a file in that language already exists.
    /// </summary>
    public bool ExtractSkipExisting { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether PGS tracks are copied out as .sup as well.
    /// </summary>
    public bool ExtractPictureSubtitles { get; set; }

    /// <summary>
    /// Gets or sets a path fragment a video must contain. Handy for trying extraction on one show first.
    /// </summary>
    public string? ExtractPathFilter { get; set; }

    /// <summary>
    /// Finds an item's sync record (the live one, not a copy). Hold <see cref="Plugin.ConfigurationLock"/>.
    /// </summary>
    public MovieSyncRecord? FindRecord(Guid itemId)
    {
        if (_recordIndex is null || _indexedCount != MovieRecords.Count)
        {
            var index = new Dictionary<Guid, MovieSyncRecord>(MovieRecords.Count);
            foreach (var record in MovieRecords)
            {
                // first one wins, same as the FirstOrDefault this replaced
                index.TryAdd(record.ItemId, record);
            }

            _recordIndex = index;
            _indexedCount = MovieRecords.Count;
        }

        return _recordIndex.GetValueOrDefault(itemId);
    }

    /// <summary>
    /// Finds an item's sync record or adds an empty one. Hold <see cref="Plugin.ConfigurationLock"/>.
    /// </summary>
    public MovieSyncRecord GetOrAddRecord(Guid itemId)
    {
        var record = FindRecord(itemId);
        if (record is null)
        {
            record = new MovieSyncRecord { ItemId = itemId };
            MovieRecords.Add(record);
            _recordIndex![itemId] = record;
            _indexedCount = MovieRecords.Count;
        }

        return record;
    }

    /// <summary>
    /// Finds an engine's settings, adding an entry if there isn't one.
    /// </summary>
    public EngineSettings GetEngineSettings(string engineId)
    {
        var existing = FindEngineSettings(engineId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new EngineSettings { EngineId = engineId };
        Engines.Add(created);
        return created;
    }

    /// <summary>
    /// Finds an engine's settings without adding anything. Use this for reads, the list is
    /// shared with the sync queue and adding to it during a save breaks the serializer.
    /// </summary>
    public EngineSettings? FindEngineSettings(string engineId)
    {
        return Engines.Find(s => string.Equals(s.EngineId, engineId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds a library's settings, adding an entry if there isn't one.
    /// </summary>
    public LibraryConfig GetLibraryConfig(Guid libraryId)
    {
        var existing = FindLibraryConfig(libraryId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new LibraryConfig { LibraryId = libraryId };
        Libraries.Add(created);
        return created;
    }

    /// <summary>
    /// Finds a library's settings without adding anything.
    /// </summary>
    public LibraryConfig? FindLibraryConfig(Guid libraryId)
    {
        return Libraries.Find(l => l.LibraryId == libraryId);
    }

    /// <summary>
    /// Says whether a library may be synced. Libraries with no entry are on.
    /// </summary>
    public bool IsLibraryEnabled(Guid libraryId)
    {
        return FindLibraryConfig(libraryId)?.Enabled ?? true;
    }

    /// <summary>
    /// Gets the sidecar suffix ready for a file name, always starting with a dot.
    /// </summary>
    public string ResolveSidecarSuffix()
    {
        var suffix = string.IsNullOrWhiteSpace(SidecarSuffix) ? ".shifted" : SidecarSuffix.Trim();
        return suffix.StartsWith('.') ? suffix : "." + suffix;
    }

    /// <summary>
    /// Says whether an ignore rule covers an item, by id (the item and its ancestors) or by path.
    /// </summary>
    public bool IsIgnored(IEnumerable<Guid> ids, string? path)
    {
        if (IgnoreRules.Count == 0)
        {
            return false;
        }

        return IsIgnored(IgnoreRules, ids as ICollection<Guid> ?? new HashSet<Guid>(ids), path);
    }

    internal static bool IsIgnored(IReadOnlyList<IgnoreRule> rules, ICollection<Guid> ids, string? path)
    {
        foreach (var rule in rules)
        {
            if (rule.ItemId is { } id && ids.Contains(id))
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

    // Matches the path itself or anything below it. The separator check stops
    // "/media/Movies" from also matching "/media/Movies Extra".
    private static bool CoversPath(string rulePath, string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        var rule = rulePath.TrimEnd('/', '\\');

        return itemPath.StartsWith(rule, StringComparison.OrdinalIgnoreCase)
            && (itemPath.Length == rule.Length || itemPath[rule.Length] is '/' or '\\');
    }

    /// <summary>
    /// Moves old single engine settings onto the LAPSE entry and patches values older
    /// versions saved. Safe to run on every start.
    /// </summary>
    public void MigrateLegacySettings()
    {
        if (!string.IsNullOrWhiteSpace(EngineBinaryPathOverride))
        {
            GetEngineSettings("lapse").PathOverride ??= EngineBinaryPathOverride;
            EngineBinaryPathOverride = null;
        }

        // 6 was the old default, only carry it over if someone changed it
        if (DefaultPenalty > 0 && DefaultPenalty != 6)
        {
            GetEngineSettings("lapse").Penalty ??= DefaultPenalty;
            DefaultPenalty = 6;
        }

        if (string.IsNullOrWhiteSpace(SidecarSuffix))
        {
            SidecarSuffix = ".shifted";
        }

        // configs from before SubtitleAppearance existed load it as null
        SubtitleAppearance ??= new SubtitleAppearance();

        SyncConfidenceThreshold = Math.Clamp(SyncConfidenceThreshold, 0, 100);

        // old configs load this as 0, which LAPSE refuses
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

    // An old version defaulted alass' encoding args to "auto", which alass 2.0 panics on.
    // Blank already means autodetect so just clear it.
    private void ClearBadAlassEncodings()
    {
        var alass = FindEngineSettings("alass");
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
