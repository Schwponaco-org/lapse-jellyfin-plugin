// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Everything about "which libraries are there, and which items in them can we sync".
/// Sync used to be movies only; now any library can be turned on and everything in it
/// with a video file and an external subtitle is fair game.
/// </summary>
public class LibraryService
{
    /// <summary>
    /// The item types worth trying to sync. Everything here has a video file with audio
    /// to line subtitles up against - there's no point offering this for an audio book.
    /// </summary>
    private static readonly BaseItemKind[] SyncableKinds =
    {
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo
    };

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    public LibraryService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Lists every library, with its LAPSE settings folded in.
    /// </summary>
    /// <returns>One entry per library, in the order Jellyfin lists them.</returns>
    public List<LibraryEntry> GetLibraries()
    {
        var config = Plugin.Instance!.Configuration;
        var result = new List<LibraryEntry>();

        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            var settings = config.Libraries.FirstOrDefault(l => l.LibraryId == id);
            var collectionType = folder.CollectionType?.ToString();

            result.Add(new LibraryEntry
            {
                ItemId = id,
                Name = folder.Name,
                CollectionType = collectionType,

                // no entry means the user has never touched this library in the dashboard,
                // and those default to on so an upgrade doesn't quietly stop syncing
                Enabled = settings?.Enabled ?? true,
                ScheduleEnabled = settings?.ScheduleEnabled ?? false,
                ScheduleFrequency = (settings?.ScheduleFrequency ?? Data.ScheduleFrequency.Daily).ToString(),
                ScheduleDay = settings?.ScheduleDay?.ToString(),
                ScheduleTime = settings?.ScheduleTime ?? "03:00",
                LastScheduledRunUtc = settings?.LastScheduledRunUtc,
                IsShowLibrary = string.Equals(collectionType, "tvshows", StringComparison.OrdinalIgnoreCase),
                Skipped = config.SkippedItemIds.Contains(id)
            });
        }

        return result;
    }

    /// <summary>
    /// Gets the ids of every library that's turned on.
    /// </summary>
    /// <returns>The enabled library ids.</returns>
    public List<Guid> GetEnabledLibraryIds()
    {
        return GetLibraries().Where(l => l.Enabled).Select(l => l.ItemId).ToList();
    }

    /// <summary>
    /// Gets the ids of every library root, which is what an item's ancestors get checked
    /// against. Worth pulling out and holding onto when looping over a lot of items -
    /// GetVirtualFolders reads the library configuration each time it's called.
    /// </summary>
    /// <returns>The set of library ids.</returns>
    public HashSet<Guid> GetLibraryIdSet()
    {
        var libraryIds = new HashSet<Guid>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                libraryIds.Add(id);
            }
        }

        return libraryIds;
    }

    /// <summary>
    /// Finds which library an item belongs to, by walking up its parents until one of
    /// them is a library root. Returns null for anything outside a library the plugin
    /// can see, which includes items that have been removed since they were queued.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="libraryIds">A set from <see cref="GetLibraryIdSet"/>, or null to
    /// build one for this call.</param>
    /// <returns>The library's CollectionFolder id, or null.</returns>
    public Guid? GetLibraryIdFor(BaseItem item, HashSet<Guid>? libraryIds = null)
    {
        libraryIds ??= GetLibraryIdSet();

        for (var current = item; current is not null; current = current.GetParent())
        {
            if (libraryIds.Contains(current.Id))
            {
                return current.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Says whether an item may be synced: right type, real file, not skipped, and in a
    /// library that's turned on.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if the item is eligible.</returns>
    public bool IsEligible(BaseItem item)
    {
        if (item.LocationType == LocationType.Virtual || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        if (!IsSyncableKind(item))
        {
            return false;
        }

        if (SyncQueueManager.IsSkipped(item))
        {
            return false;
        }

        var libraryId = GetLibraryIdFor(item);

        // an item we can't place in a library (a stray item, or a library the plugin
        // can't see) is left alone rather than synced on a guess
        return libraryId.HasValue && Plugin.Instance!.Configuration.IsLibraryEnabled(libraryId.Value);
    }

    /// <summary>
    /// Says whether an item's type is one the engines can do anything with.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if it's a video type LAPSE handles.</returns>
    public static bool IsSyncableKind(BaseItem item)
    {
        return Array.IndexOf(SyncableKinds, item.GetBaseItemKind()) >= 0;
    }

    /// <summary>
    /// Gets every syncable item in one library, or across all enabled libraries when no
    /// library is named.
    /// </summary>
    /// <param name="libraryId">The library to look in, or null for all enabled ones.</param>
    /// <param name="includeSkipped">True to include items marked as skip.</param>
    /// <returns>The items.</returns>
    public List<BaseItem> GetItems(Guid? libraryId = null, bool includeSkipped = false)
    {
        var items = new List<BaseItem>();

        if (libraryId.HasValue)
        {
            items.AddRange(QueryLibrary(libraryId.Value));
        }
        else
        {
            foreach (var id in GetEnabledLibraryIds())
            {
                items.AddRange(QueryLibrary(id));
            }
        }

        // a library can appear under more than one virtual folder path, and Jellyfin will
        // happily hand back the same item twice for that
        var seen = new HashSet<Guid>();
        var result = new List<BaseItem>();

        foreach (var item in items)
        {
            if (!seen.Add(item.Id))
            {
                continue;
            }

            if (!includeSkipped && SyncQueueManager.IsSkipped(item))
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private IEnumerable<BaseItem> QueryLibrary(Guid libraryId)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = SyncableKinds,
            Recursive = true,
            IsVirtualItem = false,
            ParentId = libraryId
        };

        return _libraryManager.GetItemList(query);
    }
}
