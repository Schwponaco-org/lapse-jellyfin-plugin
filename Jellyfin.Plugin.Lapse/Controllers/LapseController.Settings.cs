// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Settings page, per-user subtitle appearance, fonts, the *arr webhook and scheduled extraction.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Gets the server's users for the "selected users" access picker.
    /// </summary>
    [HttpGet("Lapse/Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<List<object>> GetUsers()
    {
        return _userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => (object)new
            {
                Id = u.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = u.Username,
                IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                CanManageSubtitles = u.HasPermission(PermissionKind.EnableSubtitleManagement)
            })
            .ToList();
    }

    /// <summary>
    /// Gets all the settings for the settings page.
    /// </summary>
    [HttpGet("Lapse/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<PluginSettings> GetSettings()
    {
        var config = Plugin.Instance!.Configuration;

        var settings = new PluginSettings
        {
            OutputMode = config.OutputMode,
            SidecarSuffix = config.SidecarSuffix,
            LowConfidenceAction = config.LowConfidenceAction,
            SkipAlreadyInSync = config.SkipAlreadyInSync,
            AlreadyInSyncToleranceMs = config.AlreadyInSyncToleranceMs,
            ConfidenceSigma = config.ConfidenceSigma,
            SubToSubPlacement = config.SubToSubPlacement,
            SubToSubCustomFolder = config.SubToSubCustomFolder,

            // old configs (or a format we dropped) read back as srt
            ConversionFormat = SubtitleFormats.TryNormalizeOutputFormat(config.ConversionFormat, out var storedFormat) ? storedFormat : "srt",
            ConversionReplaceOriginal = config.ConversionReplaceOriginal,
            ConversionSyncAfter = config.ConversionSyncAfter,
            ConvertBeforeSync = config.ConvertBeforeSync,
            AutomationAction = config.AutomationAction,
            AutoTranslateEnabled = config.AutoTranslateEnabled,
            AutoTranslateLanguage = config.AutoTranslateLanguage,
            AutoTranslateSkipExisting = config.AutoTranslateSkipExisting,
            SkipSyncedInUnattendedRuns = config.SkipSyncedInUnattendedRuns,
            SubtitleAccess = config.SubtitleAccess,
            SubtitleAccessUserIds = new List<string>(config.SubtitleAccessUserIds),
            OpenSubtitlesEnabled = config.OpenSubtitlesEnabled,
            OpenSubtitlesApiKey = config.OpenSubtitlesApiKey,
            OpenSubtitlesUsername = config.OpenSubtitlesUsername,
            OpenSubtitlesPassword = config.OpenSubtitlesPassword,
            OpenSubtitlesLanguage = config.OpenSubtitlesLanguage,
            MultiEngineEnabled = config.MultiEngineEnabled,
            MultiEngineUseAlass = config.MultiEngineUseAlass,
            MultiEngineUseFfsubsync = config.MultiEngineUseFfsubsync,
            MultiEngineTrigger = config.MultiEngineTrigger,
            MultiEngineCandidateFormat = config.MultiEngineCandidateFormat,
            MultiEngineInBulk = config.MultiEngineInBulk,
            ArrWebhookEnabled = config.ArrWebhookEnabled,
            ArrWebhookToken = config.ArrWebhookToken,
            ExtractEmbeddedEnabled = config.ExtractEmbeddedEnabled,
            ExtractLibraryIds = config.ExtractLibraryIds.ConvertAll(id => id.ToString("N", CultureInfo.InvariantCulture)),
            ExtractLanguages = config.ExtractLanguages,
            ExtractTrackFilter = config.ExtractTrackFilter,
            ExtractSkipExisting = config.ExtractSkipExisting,
            ExtractPictureSubtitles = config.ExtractPictureSubtitles,
            ExtractPathFilter = config.ExtractPathFilter,
            AutoUpdateEngines = config.AutoUpdateEngines,
            CountEmbeddedSubtitlesInStatus = config.CountEmbeddedSubtitlesInStatus,
            GoogleTranslateApiKey = config.GoogleTranslateApiKey,
            DeepLApiKey = config.DeepLApiKey,
            LingarrBaseUrl = config.LingarrBaseUrl,
            LingarrApiKey = config.LingarrApiKey,
            LibreTranslateBaseUrl = config.LibreTranslateBaseUrl,
            LibreTranslateApiKey = config.LibreTranslateApiKey,
            DefaultTranslationProvider = config.DefaultTranslationProvider,
            TranslationConfidenceThreshold = config.TranslationConfidenceThreshold,
            TranslationIncludeMetadataHeader = config.TranslationIncludeMetadataHeader,
            TranslationKeepLowConfidenceOriginal = config.TranslationKeepLowConfidenceOriginal,
            TranslationDefaultTargetLanguage = config.TranslationDefaultTargetLanguage,
            TranslationDefaultSourceLanguage = config.TranslationDefaultSourceLanguage,
            SubtitleFontName = config.SubtitleFontName,
            SubtitleNonLatinFontName = config.SubtitleNonLatinFontName,
            ReadableAutomation = config.ReadableAutomation,
            SubtitleFontSize = config.SubtitleFontSize,
            SubtitleLetterSpacing = config.SubtitleLetterSpacing,
            SubtitleBold = config.SubtitleBold,
            SubtitleOutline = config.SubtitleOutline,
            SubtitleMarginV = config.SubtitleMarginV,
            SubtitleAppearance = config.SubtitleAppearance
        };

        foreach (var engine in _registry.All)
        {
            var engineSettings = config.FindEngineSettings(engine.Descriptor.Id);
            var values = EngineRunner.ResolveParameters(engine);

            var entry = new EngineSettingsEntry
            {
                EngineId = engine.Descriptor.Id,
                PathOverride = engineSettings?.PathOverride,
                Penalty = engineSettings?.Penalty,
                DefaultMode = EngineRunner.ResolveDefaultMode(engine).ToString()
            };

            foreach (var parameter in engine.Descriptor.Parameters)
            {
                entry.Parameters.Add(new EngineParameterEntry { Key = parameter.Key, Value = values.GetString(parameter.Key) });
            }

            settings.Engines.Add(entry);
        }

        return settings;
    }

    /// <summary>
    /// Says what the caller may do so the web client only shows menu entries that will work.
    /// Everything is still checked again when it's called.
    /// </summary>
    [HttpGet("Lapse/Access")]
    public ActionResult<object> GetAccess()
    {
        var config = Plugin.Instance!.Configuration;
        var canEdit = CheckSubtitleAccess() is null;

        return new
        {
            CanEditSubtitles = canEdit,
            IsAdministrator = User.IsInRole(AdministratorRole),
            OpenSubtitlesEnabled = canEdit && config.OpenSubtitlesEnabled,
            OpenSubtitlesLanguage = canEdit ? config.OpenSubtitlesLanguage : null
        };
    }

    /// <summary>
    /// Gets the caller's subtitle appearance, or the server wide one if they haven't set their own.
    /// </summary>
    [HttpGet("Lapse/Appearance")]
    public ActionResult<SubtitleAppearance> GetAppearance()
    {
        var id = CallingUserKey();
        if (id is not null)
        {
            lock (Plugin.ConfigurationLock)
            {
                var mine = Plugin.Instance!.Configuration.UserSubtitleAppearances
                    .Find(a => string.Equals(a.UserId, id, StringComparison.OrdinalIgnoreCase));

                if (mine?.Appearance is { } appearance)
                {
                    return appearance;
                }
            }
        }

        return Plugin.Instance!.Configuration.SubtitleAppearance;
    }

    /// <summary>
    /// Saves the caller's own subtitle appearance. What the player's subtitle menu writes to,
    /// no admin needed and it only affects that one user.
    /// </summary>
    [HttpPost("Lapse/Appearance/Mine")]
    public ActionResult<SubtitleAppearance> SaveMyAppearance([FromBody] SubtitleAppearance appearance)
    {
        if (appearance is null)
        {
            return BadRequest("A request body is required");
        }

        var id = CallingUserKey();
        if (id is null)
        {
            return Unauthorized();
        }

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;
            var existing = config.UserSubtitleAppearances.Find(a => string.Equals(a.UserId, id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new UserSubtitleAppearance { UserId = id };
                config.UserSubtitleAppearances.Add(existing);
            }

            existing.Appearance = NormalizeAppearance(appearance, appearance.FontFamily, appearance.LetterSpacingPx);
            Plugin.Instance.SaveConfiguration();
            return existing.Appearance;
        }
    }

    /// <summary>
    /// Drops the caller's own appearance so they're back on the server wide one.
    /// </summary>
    [HttpPost("Lapse/Appearance/Mine/Reset")]
    public ActionResult<SubtitleAppearance> ResetMyAppearance()
    {
        var id = CallingUserKey();
        if (id is null)
        {
            return Unauthorized();
        }

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            if (config.UserSubtitleAppearances.RemoveAll(a => string.Equals(a.UserId, id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Plugin.Instance.SaveConfiguration();
            }

            return config.SubtitleAppearance;
        }
    }

    /// <summary>
    /// Radarr/Sonarr webhook, syncs whatever was imported. Anonymous since neither can send a
    /// Jellyfin key, so the token in the query string is the guard. Off until turned on, and a
    /// valid call can only queue a sync for a file already in the library.
    /// </summary>
    [HttpPost("Lapse/Webhook/Arr")]
    [AllowAnonymous]
    public ActionResult ArrWebhook([FromQuery] string? token, [FromBody] JsonElement payload)
    {
        var config = Plugin.Instance!.Configuration;

        if (!config.ArrWebhookEnabled || string.IsNullOrWhiteSpace(config.ArrWebhookToken))
        {
            return NotFound();
        }

        if (token is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(config.ArrWebhookToken)))
        {
            return Unauthorized();
        }

        var eventType = ArrWebhookService.ReadEventType(payload);
        ArrWebhookService.LastEvent = eventType ?? "(no event type)";
        ArrWebhookService.LastEventUtc = DateTime.UtcNow;

        // the Test button in their UI, just say yes
        if (eventType is "test" or null)
        {
            return Ok(new { Message = "LAPSE is listening." });
        }

        // only imports put a new file on disk
        if (eventType is not ("download" or "moviefileimported" or "episodefileimported" or "movieadded"))
        {
            return Ok(new { Message = "Nothing to do for " + eventType });
        }

        var paths = ArrWebhookService.ReadPaths(payload);
        if (paths.Count == 0)
        {
            return Ok(new { Message = "No video file path in that notification" });
        }

        // answer right away, both apps count a slow webhook as failed and the item isn't scanned in yet anyway
        _ = Task.Run(() => _arrWebhookService.SyncWhenScannedAsync(paths, CancellationToken.None));

        return Ok(new { Queued = paths.Count });
    }

    /// <summary>
    /// Makes a new webhook token, replacing the old one.
    /// </summary>
    [HttpPost("Lapse/Webhook/Arr/Token")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult NewArrWebhookToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        lock (Plugin.ConfigurationLock)
        {
            Plugin.Instance!.Configuration.ArrWebhookToken = token;
            Plugin.Instance.SaveConfiguration();
        }

        return Ok(new { Token = token });
    }

    /// <summary>
    /// Saves the settings page, engine tunables included.
    /// </summary>
    [HttpPost("Lapse/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveSettings([FromBody] PluginSettings settings)
    {
        if (settings is null)
        {
            return BadRequest("A settings body is required");
        }

        // outside the lock, GetVirtualFolders hits the disk
        var libraryIds = _libraryService.GetLibraryIdSet();

        lock (Plugin.ConfigurationLock)
        {
            var config = Plugin.Instance!.Configuration;

            config.OutputMode = settings.OutputMode;
            config.SidecarSuffix = Blank(settings.SidecarSuffix) ?? ".shifted";
            config.LowConfidenceAction = settings.LowConfidenceAction;
            config.SkipAlreadyInSync = settings.SkipAlreadyInSync;

            // past a few seconds it's not "already fine" anymore
            config.AlreadyInSyncToleranceMs = Math.Clamp(settings.AlreadyInSyncToleranceMs, 0, 5000);

            // LAPSE refuses <= 0, and a huge value means nothing is ever confident
            config.ConfidenceSigma = Math.Clamp(settings.ConfidenceSigma, 0.5, 30);

            config.SubToSubPlacement = settings.SubToSubPlacement;
            config.SubToSubCustomFolder = Blank(settings.SubToSubCustomFolder);
            config.ConversionFormat = SubtitleFormats.TryNormalizeOutputFormat(settings.ConversionFormat, out var conversionFormat) ? conversionFormat : "srt";
            config.ConversionReplaceOriginal = settings.ConversionReplaceOriginal;
            config.ConversionSyncAfter = settings.ConversionSyncAfter;
            config.ConvertBeforeSync = settings.ConvertBeforeSync;
            config.AutomationAction = settings.AutomationAction;
            config.AutoTranslateLanguage = Blank(settings.AutoTranslateLanguage);

            // translating into "" would run the whole library through a provider for nothing
            config.AutoTranslateEnabled = settings.AutoTranslateEnabled && config.AutoTranslateLanguage is not null;
            config.AutoTranslateSkipExisting = settings.AutoTranslateSkipExisting;
            config.SkipSyncedInUnattendedRuns = settings.SkipSyncedInUnattendedRuns;

            config.SubtitleAccess = settings.SubtitleAccess;

            // only real users, so a deleted user doesn't leave a grant behind
            config.SubtitleAccessUserIds = (settings.SubtitleAccessUserIds ?? new List<string>())
                .Select(id => Guid.TryParse(id, out var parsed) && _userManager.GetUserById(parsed) is not null ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Select(id => id.ToString("N", CultureInfo.InvariantCulture))
                .ToList();

            config.OpenSubtitlesEnabled = settings.OpenSubtitlesEnabled;
            config.OpenSubtitlesApiKey = Blank(settings.OpenSubtitlesApiKey);
            config.OpenSubtitlesUsername = Blank(settings.OpenSubtitlesUsername);
            config.OpenSubtitlesPassword = Blank(settings.OpenSubtitlesPassword);
            config.OpenSubtitlesLanguage = Blank(settings.OpenSubtitlesLanguage) ?? "en";
            config.MultiEngineEnabled = settings.MultiEngineEnabled;
            config.MultiEngineUseAlass = settings.MultiEngineUseAlass;
            config.MultiEngineUseFfsubsync = settings.MultiEngineUseFfsubsync;
            config.MultiEngineTrigger = settings.MultiEngineTrigger;
            config.MultiEngineCandidateFormat = settings.MultiEngineCandidateFormat;
            config.MultiEngineInBulk = settings.MultiEngineInBulk;
            config.ArrWebhookEnabled = settings.ArrWebhookEnabled;

            // only ids that are still libraries
            config.ExtractEmbeddedEnabled = settings.ExtractEmbeddedEnabled;
            config.ExtractLibraryIds = (settings.ExtractLibraryIds ?? new List<string>())
                .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
                .Where(libraryIds.Contains)
                .Distinct()
                .ToList();
            config.ExtractLanguages = Blank(settings.ExtractLanguages);
            config.ExtractTrackFilter = Enum.IsDefined(settings.ExtractTrackFilter) ? settings.ExtractTrackFilter : ExtractTrackFilter.All;
            config.ExtractSkipExisting = settings.ExtractSkipExisting;
            config.ExtractPictureSubtitles = settings.ExtractPictureSubtitles;
            config.ExtractPathFilter = Blank(settings.ExtractPathFilter);
            config.AutoUpdateEngines = settings.AutoUpdateEngines;
            config.CountEmbeddedSubtitlesInStatus = settings.CountEmbeddedSubtitlesInStatus;
            config.GoogleTranslateApiKey = Blank(settings.GoogleTranslateApiKey);
            config.DeepLApiKey = Blank(settings.DeepLApiKey);
            config.LingarrBaseUrl = Blank(settings.LingarrBaseUrl);
            config.LingarrApiKey = Blank(settings.LingarrApiKey);
            config.LibreTranslateBaseUrl = Blank(settings.LibreTranslateBaseUrl);
            config.LibreTranslateApiKey = Blank(settings.LibreTranslateApiKey);
            config.DefaultTranslationProvider = settings.DefaultTranslationProvider;
            config.TranslationConfidenceThreshold = Math.Clamp(settings.TranslationConfidenceThreshold, 0, 100);
            config.TranslationIncludeMetadataHeader = settings.TranslationIncludeMetadataHeader;
            config.TranslationKeepLowConfidenceOriginal = settings.TranslationKeepLowConfidenceOriginal;
            config.TranslationDefaultTargetLanguage = Blank(settings.TranslationDefaultTargetLanguage);
            config.TranslationDefaultSourceLanguage = Blank(settings.TranslationDefaultSourceLanguage);

            config.SubtitleFontName = Blank(settings.SubtitleFontName) ?? SubtitleStyle.DyslexicFontName;
            config.SubtitleNonLatinFontName = Blank(settings.SubtitleNonLatinFontName);
            config.ReadableAutomation = settings.ReadableAutomation;
            config.SubtitleFontSize = Math.Clamp(settings.SubtitleFontSize, 8, 400);
            config.SubtitleLetterSpacing = Math.Clamp(settings.SubtitleLetterSpacing, 0, 20);
            config.SubtitleBold = settings.SubtitleBold;
            config.SubtitleOutline = Math.Clamp(settings.SubtitleOutline, 0, 20);
            config.SubtitleMarginV = Math.Clamp(settings.SubtitleMarginV, 0, 500);

            if (settings.SubtitleAppearance is { } appearance)
            {
                var previous = config.SubtitleAppearance ?? new SubtitleAppearance();

                // the dashboard form has no font or spacing box, so those come in empty.
                // keep what was there instead of wiping it.
                config.SubtitleAppearance = NormalizeAppearance(
                    appearance,
                    Blank(appearance.FontFamily) ?? previous.FontFamily,
                    appearance.LetterSpacingPx > 0 ? appearance.LetterSpacingPx : previous.LetterSpacingPx);
            }

            foreach (var entry in settings.Engines ?? new List<EngineSettingsEntry>())
            {
                if (entry is null || _registry.Find(entry.EngineId) is not { } engine)
                {
                    continue;
                }

                var engineSettings = config.GetEngineSettings(engine.Descriptor.Id);
                engineSettings.PathOverride = Blank(entry.PathOverride);

                // out of range would just get refused on every run, use the engine default
                var capabilities = engine.Descriptor.Capabilities;
                engineSettings.Penalty = entry.Penalty is { } penalty && penalty >= capabilities.MinPenalty && penalty <= capabilities.MaxPenalty
                    ? penalty
                    : null;

                if (Enum.TryParse<SyncMode>(entry.DefaultMode, ignoreCase: true, out var mode)
                    && engine.Descriptor.Modes.Exists(m => m.Mode == mode))
                {
                    engineSettings.DefaultMode = mode.ToString();
                }

                // only keys this engine has, a stale form shouldn't leave junk in the config
                var known = new HashSet<string>(engine.Descriptor.Parameters.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

                engineSettings.SetParameters((entry.Parameters ?? new List<EngineParameterEntry>())
                    .Where(p => p is not null && known.Contains(p.Key))
                    .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value?.Trim())));
            }

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    /// <summary>
    /// Gets Jellyfin's subtitle font folder, what's in it and whether fallback fonts are on.
    /// </summary>
    [HttpGet("Lapse/Fonts")]
    public ActionResult<FontStatus> GetFonts()
    {
        return _fontInstaller.GetStatus();
    }

    /// <summary>
    /// Downloads OpenDyslexic into the font folder and turns fallback fonts on.
    /// </summary>
    [HttpPost("Lapse/Fonts/InstallDyslexic")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<FontStatus>> InstallDyslexicFont(CancellationToken cancellationToken)
    {
        return await _fontInstaller.InstallDyslexicFontAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the OpenDyslexic files from the font folder.
    /// </summary>
    [HttpDelete("Lapse/Fonts/Dyslexic")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<FontStatus> UninstallDyslexicFont()
    {
        return _fontInstaller.UninstallDyslexicFont();
    }

    /// <summary>
    /// Gets whether scheduled extraction is running and what the last run did.
    /// </summary>
    [HttpGet("Lapse/Extraction")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ExtractionStatus> GetExtractionStatus()
    {
        return new ExtractionStatus
        {
            Running = _extraction.IsRunning,
            LastRun = _extraction.LastRun?.Snapshot()
        };
    }

    /// <summary>
    /// Kicks off the extraction task now, through Jellyfin's scheduler so it can be stopped from there.
    /// </summary>
    [HttpPost("Lapse/Extraction/Run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RunExtraction()
    {
        if (!Plugin.Instance!.Configuration.ExtractEmbeddedEnabled)
        {
            return BadRequest("Turn extraction on and save the settings first.");
        }

        _taskManager.QueueIfNotRunning<ExtractSubtitlesTask>();
        return Ok();
    }

    // The caller's user id the way we store it, or null if we can't tell who it is.
    private string? CallingUserKey()
    {
        return GetCallingUser()?.Id.ToString("N", CultureInfo.InvariantCulture);
    }

    // Clamps everything to sane values. Used for both the per-user and server wide settings.
    private static SubtitleAppearance NormalizeAppearance(SubtitleAppearance appearance, string? fontFamily, double letterSpacingPx)
    {
        return new SubtitleAppearance
        {
            Enabled = appearance.Enabled,
            FontSizePx = Math.Clamp(appearance.FontSizePx, 8, 200),
            TextColor = NormalizeColor(appearance.TextColor, SubtitleAppearance.DefaultTextColor),
            BackgroundColor = NormalizeColor(appearance.BackgroundColor, SubtitleAppearance.DefaultBackgroundColor),
            BackgroundEnabled = appearance.BackgroundEnabled,
            FontFamily = NormalizeFontFamily(fontFamily),
            LetterSpacingPx = Math.Clamp(letterSpacingPx, 0, 20),

            // above ~85 the text goes off the top and you can't read the menu to fix it
            VerticalPositionPercent = Math.Clamp(appearance.VerticalPositionPercent, 0, 85)
        };
    }

    // These end up in a <style> block in someone's browser, so only #rgb, #rrggbb or #rrggbbaa.
    private static string NormalizeColor(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '#' || trimmed.Length is not (4 or 7 or 9))
        {
            return fallback;
        }

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                return fallback;
            }
        }

        return trimmed;
    }

    // Same deal as the colours, this goes into CSS. Font names are letters, digits, spaces
    // and a bit of punctuation, anything else (quotes, braces, newlines) gets dropped.
    internal static string? NormalizeFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, 100));
        foreach (var c in value.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            {
                builder.Append(c);
            }

            if (builder.Length == 100)
            {
                break;
            }
        }

        return builder.Length == 0 ? null : builder.ToString().Trim();
    }
}
