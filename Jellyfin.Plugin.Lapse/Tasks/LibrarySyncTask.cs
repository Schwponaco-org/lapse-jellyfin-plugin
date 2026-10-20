// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Tasks;

/// <summary>
/// Syncs every enabled library as a Jellyfin scheduled task, so it shows in the task list
/// with history and people can add their own triggers.
/// </summary>
public class LibrarySyncTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<LibrarySyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncTask"/> class.
    /// </summary>
    public LibrarySyncTask(LibraryService libraryService, SyncQueueManager queueManager, ILogger<LibrarySyncTask> logger)
    {
        _libraryService = libraryService;
        _queueManager = queueManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync subtitles";

    /// <inheritdoc />
    public string Key => "LapseLibrarySync";

    /// <inheritdoc />
    public string Description => "Runs LAPSE over every item in the libraries that are turned on in the LAPSE dashboard.";

    /// <inheritdoc />
    public string Category => "LAPSE";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default trigger. It used to run nightly and rewrote whole libraries on a fresh
        // install without anyone asking. Per-library schedules live on the dashboard, or add
        // a trigger under Scheduled Tasks.
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryService.GetItems();
        if (items.Count == 0)
        {
            _logger.LogInformation("Scheduled sync found nothing to do, no enabled library has a syncable item in it");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Scheduled sync starting over {Count} items", items.Count);
        var synced = await _queueManager.RunBatchAsync(items, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Scheduled sync finished: {Synced} of {Total} items synced without errors",
            synced,
            items.Count);
    }
}
