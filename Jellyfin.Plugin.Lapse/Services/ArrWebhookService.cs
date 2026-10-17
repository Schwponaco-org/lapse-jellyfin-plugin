// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Turns a Radarr/Sonarr import webhook into a sync.
///
/// The awkward bit is timing: they fire as soon as the import is done, but Jellyfin won't
/// know about the file until its own scan, maybe minutes later. So we wait for the item to
/// show up and sync that, and give up quietly if it never does.
/// </summary>
public class ArrWebhookService
{
    // a scan triggered by the import usually lands well inside this, slower ones get the scheduled sync
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    };

    private static readonly string[] VideoExtensions =
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv", ".mpg", ".mpeg", ".webm"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<ArrWebhookService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrWebhookService"/> class.
    /// </summary>
    public ArrWebhookService(
        ILibraryManager libraryManager,
        LibraryService libraryService,
        SyncQueueManager queueManager,
        ILogger<ArrWebhookService> logger)
    {
        _libraryManager = libraryManager;
        _libraryService = libraryService;
        _queueManager = queueManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the last event type we got. Shown on the settings page, a webhook that
    /// does nothing looks exactly like one that was never set up.
    /// </summary>
    public static string? LastEvent { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="LastEvent"/> came in.
    /// </summary>
    public static DateTime? LastEventUtc { get; set; }

    /// <summary>
    /// Pulls the video file paths out of a webhook body. Radarr and Sonarr nest things
    /// differently and keep renaming fields, so this just walks the whole document and grabs
    /// every "path" that looks like a video.
    /// </summary>
    public static List<string> ReadPaths(JsonElement payload)
    {
        var paths = new List<string>();
        Collect(payload, paths, 0);
        return paths;
    }

    /// <summary>
    /// Reads the event type (lowercased), e.g. "download" or "test".
    /// </summary>
    public static string? ReadEventType(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "eventType", "EventType" })
        {
            if (payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString()?.Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the imported files to show up in the library, then queues them. Runs
    /// detached from the request so anything that goes wrong is logged here.
    /// </summary>
    public async Task SyncWhenScannedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        // a bit of slack, jellyfin's file watcher can beat the webhook
        var since = DateTime.UtcNow.AddMinutes(-30);

        try
        {
            foreach (var path in paths)
            {
                var item = await WaitForItemAsync(path, since, cancellationToken).ConfigureAwait(false);

                if (item is null)
                {
                    _logger.LogInformation("Radarr/Sonarr reported {Path}, but Jellyfin still hasn't scanned it in. Leaving it for the scheduled sync.", path);
                }
                else if (!_libraryService.IsEligible(item))
                {
                    _logger.LogInformation("Skipping {Item} from the webhook: it's ignored, skipped, or its library is off", item.Name);
                }
                else
                {
                    _logger.LogInformation("Syncing {Item}, imported by Radarr/Sonarr", item.Name);
                    _queueManager.EnqueueItem(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the files Radarr/Sonarr reported");
        }
    }

    private static void Collect(JsonElement element, List<string> paths, int depth)
    {
        // payloads are shallow, this just stops anything silly
        if (depth > 8)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
            {
                Collect(entry, paths, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.NameEquals("path")
                && property.Value.GetString() is { Length: > 0 } value
                && LooksLikeVideo(value))
            {
                if (!paths.Exists(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(value);
                }

                continue;
            }

            Collect(property.Value, paths, depth + 1);
        }
    }

    private static bool LooksLikeVideo(string path)
    {
        var extension = Path.GetExtension(path);
        return Array.Exists(VideoExtensions, known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BaseItem?> WaitForItemAsync(string path, DateTime since, CancellationToken cancellationToken)
    {
        if (Find(path, since) is { } item)
        {
            return item;
        }

        for (var i = 0; i < RetryDelays.Length; i++)
        {
            await Task.Delay(RetryDelays[i], cancellationToken).ConfigureAwait(false);

            // only the last try looks through the whole library, the rest just check what was saved recently
            var lastTry = i == RetryDelays.Length - 1;
            if (Find(path, lastTry ? null : since) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // First by the exact path, then by "folder/file" since the *arr apps and Jellyfin often
    // have the same disk mounted in different places (/data/movies vs /movies).
    private BaseItem? Find(string path, DateTime? savedSince)
    {
        if (_libraryManager.FindByPath(path, isFolder: false) is { } exact)
        {
            return exact;
        }

        var tail = Tail(path);
        if (tail is null)
        {
            return null;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = LibraryService.SyncableKinds,
            Recursive = true,
            IsVirtualItem = false,
            MinDateLastSaved = savedSince
        };

        BaseItem? match = null;
        foreach (var candidate in _libraryManager.GetItemList(query))
        {
            if (candidate.Path is null || !string.Equals(Tail(candidate.Path), tail, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // two items ending the same way is a guess, leave it to the scheduled run
            if (match is not null && match.Id != candidate.Id)
            {
                _logger.LogInformation("More than one item ends in {Tail}, so the webhook can't tell which one it meant", tail);
                return null;
            }

            match = candidate;
        }

        if (match is not null)
        {
            _logger.LogInformation("Matched {Path} to {Item} by folder and file name, Jellyfin has it at {JellyfinPath}", path, match.Name, match.Path);
        }

        return match;
    }

    private static string? Tail(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : null;
    }
}
