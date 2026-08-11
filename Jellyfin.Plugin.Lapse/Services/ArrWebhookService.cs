// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Turns a Radarr or Sonarr import notification into a sync.
///
/// Why this shape. Both apps have a Connect entry called Webhook that POSTs a JSON body
/// to a URL of your choosing whenever something happens, so nothing here polls and nothing
/// screen-scrapes: they push, once, when a file lands. The payload is stable across
/// versions in the parts that matter, and the one field this needs - where the file went -
/// is present on every import event both apps send. That is what makes it worth doing.
///
/// The one genuinely awkward part is timing. Radarr finishes the import and fires
/// immediately, but Jellyfin doesn't know the file exists until its own scan picks it up,
/// which can be minutes later. So this doesn't sync the path directly, it waits for the
/// item to turn up in the library and syncs that. If it never turns up, it gives up
/// quietly rather than leaving something running forever.
/// </summary>
public class ArrWebhookService
{
    // How long to keep looking for the item after the notification. A library scan
    // triggered by the import usually lands well inside this; anything slower is better
    // left to the scheduled sync.
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

    /// <summary>
    /// Gets or sets what the last call from Radarr or Sonarr was, and when. A webhook that
    /// silently does nothing is indistinguishable from one that was never set up, so the
    /// settings page shows this rather than leaving people to guess from the server log.
    /// </summary>
    public static string? LastEvent { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="LastEvent"/> arrived.
    /// </summary>
    public static DateTime? LastEventUtc { get; set; }

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<ArrWebhookService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrWebhookService"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to find the item the file belongs to.</param>
    /// <param name="libraryService">Used to check the item is one we may sync.</param>
    /// <param name="queueManager">Runs the sync.</param>
    /// <param name="logger">Logger.</param>
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
    /// Reads the file paths out of a Radarr or Sonarr webhook body.
    ///
    /// Both apps nest the imported file differently and have renamed the containers over
    /// the years, so rather than modelling their schemas this walks the whole document and
    /// collects every "path" that looks like a video file. It costs nothing on a payload
    /// this size and it doesn't break when they add a field.
    /// </summary>
    /// <param name="payload">The webhook body.</param>
    /// <returns>The video file paths mentioned, in the order they appear.</returns>
    public static List<string> ReadPaths(JsonElement payload)
    {
        var paths = new List<string>();
        Collect(payload, paths, 0);
        return paths;
    }

    /// <summary>
    /// Says whether a webhook body is one of the "we just imported something" events, as
    /// opposed to a grab, a rename, a health check or the Test button.
    /// </summary>
    /// <param name="payload">The webhook body.</param>
    /// <returns>The event type, lowercased, or null when the body doesn't name one.</returns>
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
    /// Waits for a newly imported file to appear in the Jellyfin library, then syncs it.
    /// Runs in the background: the webhook call itself answers straight away, because
    /// Radarr and Sonarr both treat a slow webhook as a failed one.
    /// </summary>
    /// <param name="paths">The file paths from the notification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task SyncWhenScannedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths)
        {
            var item = await WaitForItemAsync(path, cancellationToken).ConfigureAwait(false);

            if (item is null)
            {
                _logger.LogInformation(
                    "Radarr/Sonarr reported {Path}, but Jellyfin still hasn't scanned it in. Leaving it for the scheduled sync.",
                    path);
                continue;
            }

            if (!_libraryService.IsEligible(item))
            {
                _logger.LogInformation("Skipping {Item} from the webhook: it's ignored, skipped, or its library is off", item.Name);
                continue;
            }

            _logger.LogInformation("Syncing {Item}, imported by Radarr/Sonarr", item.Name);
            _queueManager.EnqueueItem(item);
        }
    }

    private static void Collect(JsonElement element, List<string> paths, int depth)
    {
        // the payloads are shallow; a guard here just stops anything pathological
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.NameEquals("path")
                        && property.Value.GetString() is { Length: > 0 } value
                        && LooksLikeVideo(value)
                        && !paths.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(value);
                        continue;
                    }

                    Collect(property.Value, paths, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                {
                    Collect(entry, paths, depth + 1);
                }

                break;

            default:
                break;
        }
    }

    private static bool LooksLikeVideo(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Length is > 1 and < 6
            && Array.Exists(VideoExtensions, known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BaseItem?> WaitForItemAsync(string path, CancellationToken cancellationToken)
    {
        var item = _libraryManager.FindByPath(path, isFolder: false);
        if (item is not null)
        {
            return item;
        }

        foreach (var delay in RetryDelays)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            item = _libraryManager.FindByPath(path, isFolder: false);
            if (item is not null)
            {
                return item;
            }
        }

        return null;
    }
}
