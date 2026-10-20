// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// API for the dashboard and the injected context menu.
///
/// Reads just need a logged in user. Changing the plugin itself (settings, engines,
/// libraries, bulk runs) needs an admin. Working on one item's subtitles depends on
/// the Access setting, see <see cref="CheckSubtitleAccess"/>.
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public partial class LapseController : ControllerBase
{
    // Jellyfin's own constants for these live in the server assembly, which we can't reference.
    private const string AdministratorRole = "Administrator";
    private const string UserIdClaim = "Jellyfin-UserId";

    private const string AccessDeniedMessage =
        "Only administrators can do this. An admin can open it up to other users under Access in the LAPSE settings.";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly EngineInstaller _installer;
    private readonly EngineCapabilityProbe _probe;
    private readonly EngineUpdater _updater;
    private readonly SubtitleLocator _subtitleLocator;
    private readonly SubtitleShifter _subtitleShifter;
    private readonly SubtitleConverter _converter;
    private readonly SubtitleExtractor _extractor;
    private readonly SubtitleRemuxer _remuxer;
    private readonly FontInstaller _fontInstaller;
    private readonly TranslationService _translationService;
    private readonly SeriesSyncService _seriesSyncService;
    private readonly OpenSubtitlesService _openSubtitles;
    private readonly ArrWebhookService _arrWebhookService;
    private readonly SyncHistoryService _historyService;
    private readonly ReadableSubtitleService _readable;
    private readonly MultiEngineSyncService _multiEngine;
    private readonly ISessionManager _sessionManager;
    private readonly EmbeddedSubtitleExtractionService _extraction;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LapseController"/> class.
    /// </summary>
    public LapseController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        LibraryService libraryService,
        SyncQueueManager queueManager,
        EngineRegistry registry,
        EngineRunner runner,
        EngineInstaller installer,
        EngineCapabilityProbe probe,
        EngineUpdater updater,
        SubtitleLocator subtitleLocator,
        SubtitleShifter subtitleShifter,
        SubtitleConverter converter,
        SubtitleExtractor extractor,
        SubtitleRemuxer remuxer,
        FontInstaller fontInstaller,
        TranslationService translationService,
        SeriesSyncService seriesSyncService,
        OpenSubtitlesService openSubtitles,
        ArrWebhookService arrWebhookService,
        SyncHistoryService historyService,
        ReadableSubtitleService readable,
        MultiEngineSyncService multiEngine,
        ISessionManager sessionManager,
        EmbeddedSubtitleExtractionService extraction,
        ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _libraryService = libraryService;
        _queueManager = queueManager;
        _registry = registry;
        _runner = runner;
        _installer = installer;
        _probe = probe;
        _updater = updater;
        _subtitleLocator = subtitleLocator;
        _subtitleShifter = subtitleShifter;
        _converter = converter;
        _extractor = extractor;
        _remuxer = remuxer;
        _fontInstaller = fontInstaller;
        _translationService = translationService;
        _seriesSyncService = seriesSyncService;
        _openSubtitles = openSubtitles;
        _arrWebhookService = arrWebhookService;
        _historyService = historyService;
        _readable = readable;
        _multiEngine = multiEngine;
        _sessionManager = sessionManager;
        _extraction = extraction;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Checks whether the caller may work on an item's subtitles. Admins always can,
    /// everyone else depends on the Access setting.
    /// </summary>
    private ActionResult? CheckSubtitleAccess()
    {
        if (User.IsInRole(AdministratorRole))
        {
            return null;
        }

        var config = Plugin.Instance!.Configuration;
        var allowed = config.SubtitleAccess switch
        {
            SubtitleAccessMode.Everyone => true,
            SubtitleAccessMode.SubtitleManagers => GetCallingUser()?.HasPermission(PermissionKind.EnableSubtitleManagement) == true,
            SubtitleAccessMode.SelectedUsers => GetCallingUser() is { } user
                && config.SubtitleAccessUserIds.Exists(id => Guid.TryParse(id, out var parsed) && parsed.Equals(user.Id)),
            _ => false
        };

        return allowed ? null : StatusCode(StatusCodes.Status403Forbidden, AccessDeniedMessage);
    }

    // Tries the user id claim first, then the user name (always on the token, unique server wide).
    private User? GetCallingUser()
    {
        var id = User.FindFirst(UserIdClaim)?.Value;
        if (Guid.TryParse(id, out var userId) && !userId.Equals(default)
            && _userManager.GetUserById(userId) is { } byId)
        {
            return byId;
        }

        var name = User.Identity?.Name;
        return string.IsNullOrEmpty(name) ? null : _userManager.GetUserByName(name);
    }

    // Looks an item up for the caller. Non-admins only see what they could open in Jellyfin,
    // anything else looks like it doesn't exist. Jellyfin throws on an empty id so catch that here.
    private BaseItem? GetVisibleItem(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || User.IsInRole(AdministratorRole))
        {
            return item;
        }

        var user = GetCallingUser();
        return user is not null && item.IsVisibleStandalone(user) ? item : null;
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Finds one of an item's subtitles by path and hands back our copy of the path, not
    /// the caller's. Everything that writes a subtitle goes through here so a request can't
    /// point at some random file.
    /// </summary>
    private (BaseItem? Item, SubtitleOption? Subtitle) FindSubtitle(Guid itemId, string? path)
    {
        var item = GetVisibleItem(itemId);
        if (item is null || string.IsNullOrWhiteSpace(path))
        {
            return (item, null);
        }

        return (item, _subtitleLocator.GetExternalSubtitles(item)
            .Find(s => string.Equals(s.Path, path, StringComparison.Ordinal)));
    }
}
