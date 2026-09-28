// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Lapse.Tasks;

/// <summary>
/// Writes the embedded subtitle tracks picked in LAPSE's settings out as files beside the
/// video. Experimental, and does nothing until it's turned on there.
/// </summary>
public class ExtractSubtitlesTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly EmbeddedSubtitleExtractionService _extraction;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractSubtitlesTask"/> class.
    /// </summary>
    /// <param name="extraction">Does the work.</param>
    public ExtractSubtitlesTask(EmbeddedSubtitleExtractionService extraction)
    {
        _extraction = extraction;
    }

    /// <inheritdoc />
    public string Name => "Extract embedded subtitles";

    /// <inheritdoc />
    public string Key => "LapseExtractSubtitles";

    /// <inheritdoc />
    public string Description => "Writes the embedded subtitle tracks picked under LAPSE's Experimental settings out as files beside each video. Does nothing until it's turned on there.";

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
        // An hour ahead of the nightly sync. Nothing the sync does depends on it - freshly
        // extracted tracks are left out of automatic syncs - but it keeps the two heavy
        // reads of the library from landing on the disks at the same moment.
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _extraction.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
