// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Everything that only makes sense for a whole series or season: finding the episodes
/// under one, and working out what "the reference subtitle" means across all of them.
///
/// A reference for a single item is one file. Across a series it can't be, since every
/// episode has its own files, so it's a track key instead - a language code, or the bit
/// of the file name that follows the episode's own name. Each episode then lines its
/// other subtitles up against whichever of its files carries that key.
/// </summary>
public class SeriesSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleLocator _subtitleLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesSyncService"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to enumerate episodes.</param>
    /// <param name="subtitleLocator">Finds an episode's external subtitles.</param>
    public SeriesSyncService(ILibraryManager libraryManager, SubtitleLocator subtitleLocator)
    {
        _libraryManager = libraryManager;
        _subtitleLocator = subtitleLocator;
    }

    /// <summary>
    /// Says whether an item is something this service can expand into episodes.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True for a series or a season.</returns>
    public static bool IsSeriesOrSeason(BaseItem item)
    {
        return item is Series or Season;
    }

    /// <summary>
    /// Gets every episode under a series or season, in broadcast order.
    /// </summary>
    /// <param name="item">The series or season.</param>
    /// <returns>The episodes, or an empty list for anything else.</returns>
    public List<BaseItem> GetEpisodes(BaseItem item)
    {
        if (item is not Folder folder)
        {
            return new List<BaseItem>();
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            ParentId = folder.Id
        };

        return _libraryManager.GetItemList(query)
            .Where(episode => !string.IsNullOrEmpty(episode.Path))
            .OrderBy(episode => (episode as Episode)?.ParentIndexNumber ?? 0)
            .ThenBy(episode => (episode as Episode)?.IndexNumber ?? 0)
            .ToList();
    }

    /// <summary>
    /// Lists the series in one library, for the dashboard's series picker.
    /// </summary>
    /// <param name="libraryId">The library's CollectionFolder id.</param>
    /// <returns>The series, by name.</returns>
    public List<BaseItem> GetSeries(Guid libraryId)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            ParentId = libraryId
        };

        return _libraryManager.GetItemList(query)
            .OrderBy(series => series.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lists the seasons of one series, in order.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>The seasons, or an empty list for anything that isn't a series.</returns>
    public List<BaseItem> GetSeasons(BaseItem series)
    {
        if (series is not Folder folder)
        {
            return new List<BaseItem>();
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Season },
            Recursive = true,
            IsVirtualItem = false,
            ParentId = folder.Id
        };

        return _libraryManager.GetItemList(query)
            .OrderBy(season => season.IndexNumber ?? int.MaxValue)
            .ThenBy(season => season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Works out which subtitle tracks are available across a series, so the reference
    /// dropdown can offer something that means the same thing on every episode.
    /// </summary>
    /// <param name="item">The series or season.</param>
    /// <returns>One option per distinct track key, most widely available first.</returns>
    public List<ReferenceOption> GetReferenceOptions(BaseItem item)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var episodes = GetEpisodes(item);

        foreach (var episode in episodes)
        {
            // one vote per episode, so an episode with two English files doesn't make
            // English look twice as common as it is
            var keysHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var subtitle in _subtitleLocator.GetExternalSubtitles(episode))
            {
                var key = BuildKey(episode, subtitle);
                if (key is not null)
                {
                    keysHere.Add(key);
                }
            }

            foreach (var key in keysHere)
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ReferenceOption
            {
                Key = pair.Key,
                EpisodeCount = pair.Value,
                TotalEpisodes = episodes.Count
            })
            .ToList();
    }

    /// <summary>
    /// Picks the subtitle on one episode that matches a reference key.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="subtitles">That episode's external subtitles.</param>
    /// <param name="key">The reference key from <see cref="GetReferenceOptions"/>.</param>
    /// <returns>The matching subtitle, or null when this episode doesn't have one.</returns>
    public static SubtitleOption? MatchReference(BaseItem episode, IReadOnlyList<SubtitleOption> subtitles, string key)
    {
        return subtitles.FirstOrDefault(s => string.Equals(BuildKey(episode, s), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the key that identifies "the same subtitle track" across episodes: the
    /// language when Jellyfin knows it, otherwise whatever the file name carries after
    /// the episode's own name (the ".en.forced" in "Show S01E01.en.forced.srt").
    /// </summary>
    /// <param name="episode">The episode the subtitle belongs to.</param>
    /// <param name="subtitle">The subtitle.</param>
    /// <returns>The key, or null when there's nothing to tell it apart by.</returns>
    private static string? BuildKey(BaseItem episode, SubtitleOption subtitle)
    {
        if (!string.IsNullOrWhiteSpace(subtitle.Language))
        {
            return subtitle.Language.ToLowerInvariant();
        }

        var fileName = Path.GetFileNameWithoutExtension(subtitle.Path);
        var stem = Path.GetFileNameWithoutExtension(episode.Path ?? string.Empty);

        if (!string.IsNullOrEmpty(stem)
            && fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && fileName.Length > stem.Length)
        {
            return fileName[stem.Length..].TrimStart('.').ToLowerInvariant();
        }

        // A subtitle named nothing like its episode can't be matched to the same track on
        // another episode, so it isn't offered as a reference at all.
        return null;
    }
}
