// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// A copy of the skip and ignore lists. Grab one with <see cref="Load"/> before walking a
/// library so each item costs one trip up its parents instead of four, and so the lists
/// can't change under us halfway through.
/// </summary>
public sealed class ItemExclusions
{
    private static readonly ItemExclusions None = new(new HashSet<Guid>(), Array.Empty<IgnoreRule>());

    private readonly HashSet<Guid> _skipped;
    private readonly IgnoreRule[] _ignored;

    private ItemExclusions(HashSet<Guid> skipped, IgnoreRule[] ignored)
    {
        _skipped = skipped;
        _ignored = ignored;
    }

    /// <summary>
    /// Copies the current lists out of the config.
    /// </summary>
    public static ItemExclusions Load()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return None;
        }

        lock (Plugin.ConfigurationLock)
        {
            if (config.SkippedItemIds.Count == 0 && config.IgnoreRules.Count == 0)
            {
                return None;
            }

            return new ItemExclusions(new HashSet<Guid>(config.SkippedItemIds), config.IgnoreRules.ToArray());
        }
    }

    /// <summary>
    /// Says whether an item is ignored or skipped. Ignore wins since it's the stronger rule.
    /// </summary>
    /// <returns><see cref="MovieSyncStatus.Ignored"/>, <see cref="MovieSyncStatus.Skipped"/> or null.</returns>
    public MovieSyncStatus? Check(BaseItem item)
    {
        if (_skipped.Count == 0 && _ignored.Length == 0)
        {
            return null;
        }

        var chain = new List<Guid>(8);
        for (var current = item; current is not null; current = current.GetParent())
        {
            chain.Add(current.Id);
        }

        // The parent chain follows the folders on disk (Movie > /media/movies > root) and the
        // library's CollectionFolder isn't on it, so a skip or ignore on a whole library
        // never matched. Ask Jellyfin which libraries the item is in as well.
        if (BaseItem.LibraryManager is { } libraryManager)
        {
            foreach (var folder in libraryManager.GetCollectionFolders(item))
            {
                chain.Add(folder.Id);
            }
        }

        if (_ignored.Length > 0 && PluginConfiguration.IsIgnored(_ignored, chain, item.Path))
        {
            return MovieSyncStatus.Ignored;
        }

        if (_skipped.Count > 0 && chain.Exists(_skipped.Contains))
        {
            return MovieSyncStatus.Skipped;
        }

        return null;
    }

    /// <summary>
    /// Shorthand for "is this item off limits to automatic runs".
    /// </summary>
    public bool Excludes(BaseItem item) => Check(item) is not null;
}
