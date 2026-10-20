// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Which libraries exist and which items in them we can sync.
/// </summary>
public class LibraryService
{
    // Things with a video track to line subtitles up against.
    internal static readonly BaseItemKind[] SyncableKinds =
    {
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo
    };

    // GetVirtualFolders reads every library's options off disk, and a library scan asks
    // which library each new item is in. Libraries don't change that often.
    private static readonly TimeSpan LibraryIdsCacheFor = TimeSpan.FromSeconds(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    // swapped as a whole so readers never see half an update
    private LibraryIdSnapshot? _libraryIds;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryService"/> class.
    /// </summary>
    public LibraryService(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Lists every library with our settings folded in, in Jellyfin's order.
    /// </summary>
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

            var settings = config.FindLibraryConfig(id);
            var collectionType = folder.CollectionType?.ToString();

            result.Add(new LibraryEntry
            {
                ItemId = id,
                Name = folder.Name,
                CollectionType = collectionType,

                // untouched libraries are on, but auto-sync and schedules stay off until
                // someone ticks them. An update shouldn't start rewriting files by itself.
                Enabled = settings?.Enabled ?? true,
                AutoSyncEnabled = settings?.AutoSyncEnabled ?? false,
                ScheduleEnabled = settings?.ScheduleEnabled ?? false,
                ScheduleFrequency = (settings?.ScheduleFrequency ?? ScheduleFrequency.Daily).ToString(),
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
    /// Gets the ids of every library root. Cached for a few seconds since it's asked per item.
    /// </summary>
    public IReadOnlySet<Guid> GetLibraryIdSet()
    {
        if (_libraryIds is { } cached && DateTime.UtcNow - cached.ReadUtc < LibraryIdsCacheFor)
        {
            return cached.Ids;
        }

        var libraryIds = new HashSet<Guid>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                libraryIds.Add(id);
            }
        }

        _libraryIds = new LibraryIdSnapshot(libraryIds, DateTime.UtcNow);
        return libraryIds;
    }

    /// <summary>
    /// Finds which library an item is in, or null if it's outside every library we can see.
    /// </summary>
    public Guid? GetLibraryIdFor(BaseItem item, IReadOnlySet<Guid>? libraryIds = null)
    {
        libraryIds ??= GetLibraryIdSet();

        for (var current = item; current is not null; current = current.GetParent())
        {
            if (libraryIds.Contains(current.Id))
            {
                return current.Id;
            }
        }

        // The parent chain follows the folders on disk and the CollectionFolder isn't
        // always on it, depends on how the library paths were set up. Jellyfin's own
        // lookup is what the item queries use, so fall back to that.
        foreach (var folder in _libraryManager.GetCollectionFolders(item))
        {
            if (libraryIds.Contains(folder.Id))
            {
                return folder.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Says whether an item may be synced: right type, real file, not skipped or ignored,
    /// and in a library that's turned on.
    /// </summary>
    public bool IsEligible(BaseItem item)
    {
        return EligibleLibrary(item) is not null;
    }

    /// <summary>
    /// Says whether a new item should be picked up right away. Needs auto-sync ticked on
    /// its library, being eligible isn't enough.
    /// </summary>
    public bool IsAutoSyncEnabled(BaseItem item)
    {
        return EligibleLibrary(item) is { } id
            && Plugin.Instance!.Configuration.FindLibraryConfig(id)?.AutoSyncEnabled == true;
    }

    private Guid? EligibleLibrary(BaseItem item)
    {
        if (item.LocationType == LocationType.Virtual
            || string.IsNullOrEmpty(item.Path)
            || !IsSyncableKind(item)
            || ItemExclusions.Load().Excludes(item))
        {
            return null;
        }

        // can't place it in a library? leave it alone rather than guess
        var libraryId = GetLibraryIdFor(item);
        return libraryId is { } id && Plugin.Instance!.Configuration.IsLibraryEnabled(id) ? id : null;
    }

    /// <summary>
    /// Says whether an item's type is one the engines can work with.
    /// </summary>
    public static bool IsSyncableKind(BaseItem item)
    {
        return Array.IndexOf(SyncableKinds, item.GetBaseItemKind()) >= 0;
    }

    /// <summary>
    /// Gets every syncable item in one library, or in all enabled libraries.
    /// </summary>
    /// <param name="libraryId">The library, or null for every enabled one.</param>
    /// <param name="includeSkipped">True to keep skipped and ignored items (the status list shows them greyed out).</param>
    public List<BaseItem> GetItems(Guid? libraryId = null, bool includeSkipped = false)
    {
        return GetItemsWithLibrary(libraryId, includeSkipped).ConvertAll(e => e.Item);
    }

    /// <summary>
    /// Same as <see cref="GetItems"/> but each item comes with the library it was found in.
    /// That's taken from the query rather than a parent walk, which sometimes comes up empty.
    /// </summary>
    public List<(BaseItem Item, Guid LibraryId)> GetItemsWithLibrary(Guid? libraryId = null, bool includeSkipped = false)
    {
        var searched = new List<Guid>();
        if (libraryId.HasValue)
        {
            searched.Add(libraryId.Value);
        }
        else
        {
            foreach (var library in GetLibraries())
            {
                if (library.Enabled)
                {
                    searched.Add(library.ItemId);
                }
            }
        }

        var exclusions = includeSkipped ? null : ItemExclusions.Load();

        // the same item can come back twice when a library has overlapping paths
        var seen = new HashSet<Guid>();
        var result = new List<(BaseItem Item, Guid LibraryId)>();

        foreach (var id in searched)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = SyncableKinds,
                Recursive = true,
                IsVirtualItem = false,
                ParentId = id
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (seen.Add(item.Id) && exclusions?.Excludes(item) != true)
                {
                    result.Add((item, id));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Finds other copies of a film or episode that a subtitle could be moved from: the other
    /// versions Jellyfin groups with it, and items in any library with the same IMDb, TMDb or
    /// TVDb id. Only real files, and for a user only what they can see.
    /// </summary>
    public List<(Video Copy, bool IsVersion)> FindOtherCopies(BaseItem item, User? user)
    {
        var found = new List<(Video Copy, bool IsVersion)>();
        if (item is not Video video || string.IsNullOrEmpty(video.Path))
        {
            return found;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal) { video.Path };

        void Add(BaseItem? candidate, bool isVersion)
        {
            if (candidate is Video copy && !copy.IsVirtualItem && !string.IsNullOrEmpty(copy.Path)
                && File.Exists(copy.Path) && paths.Add(copy.Path))
            {
                found.Add((copy, isVersion));
            }
        }

        // We can be handed either end of a version group, so start from the primary. The
        // versions come with the item, a user who can see one can see them all.
        //
        // Version groups changed shape in Jellyfin 12 (PrimaryVersionId became a Guid and the
        // versions moved out of the item into their own table), so this goes through the media
        // sources, the same list the player's version picker shows, which both have.
        var primary = ReadPrimaryVersionId(video) is { } primaryId && _libraryManager.GetItemById(primaryId) is Video main
            ? main
            : FindLocalPrimary(video) ?? video;

        Add(primary, true);

        foreach (var source in _mediaSourceManager.GetStaticMediaSources(primary, false))
        {
            if (Guid.TryParse(source.Id, out var versionId))
            {
                Add(_libraryManager.GetItemById(versionId), true);
            }
        }

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in new[] { MetadataProvider.Imdb, MetadataProvider.Tmdb, MetadataProvider.Tvdb })
        {
            var value = item.GetProviderId(provider);
            if (!string.IsNullOrWhiteSpace(value) && value != "0")
            {
                ids[provider.ToString()] = value;
            }
        }

        if (ids.Count == 0)
        {
            return found;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = ids,
            IsVirtualItem = false,
            Recursive = true
        };

        foreach (var other in _libraryManager.GetItemList(query))
        {
            if (user is null || other.IsVisibleStandalone(user))
            {
                Add(other, false);
            }
        }

        return found;
    }

    // A string on 10.11 and a Guid? on 12, so it's read without binding to either
    private static Guid? ReadPrimaryVersionId(Video video)
    {
        var value = typeof(Video).GetProperty(nameof(Video.PrimaryVersionId))?.GetValue(video)?.ToString();
        return Guid.TryParse(value, out var id) ? id : null;
    }

    // A version found next to the main file ("Film - TV.mkv" beside "Film - Bluray.mkv") has
    // no PrimaryVersionId pointing back on 10.11, only the main item lists it. So look at the
    // videos in the same folder for the one whose versions include this file.
    private Video? FindLocalPrimary(Video video)
    {
        var folder = Path.GetDirectoryName(video.Path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (string.Equals(file, video.Path, StringComparison.Ordinal) || SubtitleFormats.IsSubtitle(file))
            {
                continue;
            }

            if (_libraryManager.FindByPath(file, false) is Video candidate
                && _mediaSourceManager.GetStaticMediaSources(candidate, false).Any(s => string.Equals(s.Path, video.Path, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record LibraryIdSnapshot(HashSet<Guid> Ids, DateTime ReadUtc);
}
