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
/// Syncs every enabled library, as a task Jellyfin's own scheduler owns. Having it here
/// means the task list shows it, the run history is kept, and people can add whatever
/// triggers they like on top of the daily default.
/// </summary>
public class LibrarySyncTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly LibraryService _libraryService;
    private readonly SyncQueueManager _queueManager;
    private readonly ILogger<LibrarySyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncTask"/> class.
    /// </summary>
    /// <param name="libraryService">Works out which items to sync.</param>
    /// <param name="queueManager">Runs the syncs.</param>
    /// <param name="logger">Logger.</param>
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
        // No schedule of its own. This used to run every night at 03:00 over every library
        // that was turned on, which on a fresh install meant rewriting the whole library
        // the first night without anybody asking, against everything else in LAPSE that
        // stays off until someone turns it on. A library that should be synced on a timer
        // gets that from its own schedule on the dashboard, and anyone who wants this one
        // on a timer can add one under Scheduled Tasks.
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryService.GetItems();
        if (items.Count == 0)
        {
            _logger.LogInformation("Scheduled sync found nothing to do - no enabled library has a syncable item in it");
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
