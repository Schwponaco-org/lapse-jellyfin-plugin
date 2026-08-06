// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Tasks;

/// <summary>
/// Checks GitHub once a day for newer engine releases and installs them, for every
/// engine that has auto-update turned on. Engines behind a custom binary path are left
/// alone: that binary isn't the plugin's to replace.
/// </summary>
public class EngineUpdateTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly EngineUpdater _updater;
    private readonly ILogger<EngineUpdateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineUpdateTask"/> class.
    /// </summary>
    /// <param name="updater">Does the checking and installing.</param>
    /// <param name="logger">Logger.</param>
    public EngineUpdateTask(EngineUpdater updater, ILogger<EngineUpdateTask> logger)
    {
        _updater = updater;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update sync engines";

    /// <inheritdoc />
    public string Key => "LapseEngineUpdate";

    /// <inheritdoc />
    public string Description => "Looks for newer releases of the LAPSE sync engines and installs them.";

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
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var results = await _updater.RunAutoUpdatesAsync(progress, cancellationToken).ConfigureAwait(false);

        foreach (var (engineId, outcome) in results)
        {
            _logger.LogInformation("Engine auto-update - {Engine}: {Outcome}", engineId, outcome);
        }

        progress.Report(100);
    }
}
