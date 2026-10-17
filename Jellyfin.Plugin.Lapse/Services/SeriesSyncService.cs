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
/// Series/season stuff: finding the episodes, and what "the reference subtitle" means
/// across them. Each episode has its own files, so the reference is a track key (language
/// code, or whatever follows the episode name in the file name) instead of a file.
/// </summary>
public class SeriesSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleLocator _subtitleLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesSyncService"/> class.
    /// </summary>
    public SeriesSyncService(ILibraryManager libraryManager, SubtitleLocator subtitleLocator)
    {
        _libraryManager = libraryManager;
        _subtitleLocator = subtitleLocator;
    }

    /// <summary>
    /// Says whether an item is something this service can expand into episodes.
    /// </summary>
    public static bool IsSeriesOrSeason(BaseItem item)
    {
        return item is Series or Season;
    }

    /// <summary>
    /// Gets every episode under a series or season, in broadcast order.
    /// </summary>
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
    /// Works out which tracks exist across a series for the reference dropdown.
    /// </summary>
    public List<ReferenceOption> GetReferenceOptions(BaseItem item)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var episodes = GetEpisodes(item);

        foreach (var episode in episodes)
        {
            // one vote per episode, so an episode with two English files doesn't make
            // English look twice as common as it is
            var keysHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // same filter as SyncQueueManager.SyncOneAsync, otherwise we'd offer references
            // the run then throws away
            var subtitles = _subtitleLocator.GetExternalSubtitles(episode);
            var paths = subtitles.ConvertAll(s => s.Path);

            foreach (var subtitle in subtitles)
            {
                if (subtitle.IsEmbedded
                    || !subtitle.Supported
                    || SubtitleExtractor.IsUntouchedExtraction(episode, subtitle.Path)
                    || Engines.EngineRunner.IsSidecarOfAnother(subtitle.Path, paths))
                {
                    continue;
                }

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
    public static SubtitleOption? MatchReference(BaseItem episode, IReadOnlyList<SubtitleOption> subtitles, string key)
    {
        return subtitles.FirstOrDefault(s => string.Equals(BuildKey(episode, s), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Key for "the same track" across episodes: the language if known, otherwise what
    /// follows the episode name in the file name (".en.forced" in "Show S01E01.en.forced.srt").
    /// </summary>
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

        // named nothing like the episode, can't match it across episodes
        return null;
    }
}
