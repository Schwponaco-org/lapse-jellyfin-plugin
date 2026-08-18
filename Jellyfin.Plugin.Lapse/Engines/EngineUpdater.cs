// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Keeps installed engines up to date against their GitHub releases. Doing the check
/// here rather than in the controller means the scheduled task and the dashboard button
/// take exactly the same path.
/// </summary>
public class EngineUpdater
{
    private readonly EngineRegistry _registry;
    private readonly EngineRunner _runner;
    private readonly EngineInstaller _installer;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly ILogger<EngineUpdater> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineUpdater"/> class.
    /// </summary>
    /// <param name="registry">The known engines.</param>
    /// <param name="runner">Used to check whether an engine is installed at all.</param>
    /// <param name="installer">Does the actual downloading.</param>
    /// <param name="releaseClient">Asks GitHub what the latest release is.</param>
    /// <param name="logger">Logger.</param>
    public EngineUpdater(
        EngineRegistry registry,
        EngineRunner runner,
        EngineInstaller installer,
        GitHubReleaseClient releaseClient,
        ILogger<EngineUpdater> logger)
    {
        _registry = registry;
        _runner = runner;
        _installer = installer;
        _releaseClient = releaseClient;
        _logger = logger;
    }

    /// <summary>
    /// Works out where an engine stands: what's installed, what's published, and whether
    /// those differ.
    /// </summary>
    /// <param name="engine">The engine to check.</param>
    /// <param name="force">True to ignore the cached answer from GitHub.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The update status.</returns>
    public async Task<EngineUpdateStatus> CheckAsync(IEngine engine, bool force = false, CancellationToken cancellationToken = default)
    {
        var installedPath = _runner.ResolvePath(engine);
        var installed = File.Exists(installedPath);
        var installedVersion = installed
            ? await ResolveInstalledVersionAsync(engine, cancellationToken).ConfigureAwait(false)
            : null;

        var settings = Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id);

        var status = new EngineUpdateStatus
        {
            EngineId = engine.Descriptor.Id,
            InstalledVersion = installedVersion,
            AutoUpdate = Plugin.Instance!.Configuration.AutoUpdateEngines,
            LastCheckedUtc = settings.LastUpdateCheckUtc
        };

        var latest = await _releaseClient.GetLatestTagAsync(engine.Descriptor.GitHubRepo, force, cancellationToken).ConfigureAwait(false);
        status.LatestVersion = latest;

        if (latest is not null)
        {
            // The dashboard runs this for every engine on every load, so only write the
            // config back when the answer actually moved. Saving three times a page load
            // to record the same tag would be a file write for nothing.
            var changed = !string.Equals(settings.LatestKnownVersion, latest, StringComparison.Ordinal);

            settings.LatestKnownVersion = latest;
            status.LastCheckedUtc = settings.LastUpdateCheckUtc;

            if (changed)
            {
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                status.LastCheckedUtc = settings.LastUpdateCheckUtc;
                Plugin.Instance!.SaveConfiguration();
            }
        }

        status.VersionUnknown = installed && installedVersion is null;
        status.UpdateAvailable = installed

            // A binary behind a path override is not ours to replace. Offering an update
            // for one was worse than saying nothing: installing writes to the managed
            // path, which is not the file being run, so the button appeared to do nothing
            // and the same update kept being offered afterwards.
            && string.IsNullOrWhiteSpace(settings.PathOverride)
            && engine.Descriptor.GetDownloadForThisMachine() is not null
            && latest is not null

            // An engine we can't put a version on is treated as out of date rather than
            // as up to date. Saying "already on an unknown version" and refusing to do
            // anything is the worst of both, and it's what this used to do: the version
            // was only ever recorded at install time, so any engine installed before that
            // existed - or installed while GitHub was unreachable - could never be updated
            // again from the dashboard.
            && (installedVersion is null || GitHubReleaseClient.IsNewer(installedVersion, latest));

        return status;
    }

    /// <summary>
    /// Works out which version of an engine is on disk. The release tag recorded at
    /// install time is the first choice, since that's the thing a release compares
    /// against, but a binary that got there some other way can still be asked what it is,
    /// and an answer from the binary is better than no answer at all.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or null when neither source knows.</returns>
    public async Task<string?> ResolveInstalledVersionAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        var settings = Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id);

        if (!string.IsNullOrWhiteSpace(settings.InstalledVersion))
        {
            return settings.InstalledVersion;
        }

        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(runtime.Version))
        {
            return null;
        }

        // Write it down so the next check doesn't have to start the binary again, and so
        // the card has something to show before any probing has happened.
        settings.InstalledVersion = runtime.Version;
        Plugin.Instance!.SaveConfiguration();
        return runtime.Version;
    }

    /// <summary>
    /// Replaces an engine's binary with the newest release.
    /// </summary>
    /// <param name="engine">The engine to update.</param>
    /// <param name="force">True to reinstall even when the versions look the same.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, as a line to show the user.</returns>
    public async Task<string> UpdateAsync(IEngine engine, bool force = false, CancellationToken cancellationToken = default)
    {
        var status = await CheckAsync(engine, force: true, cancellationToken).ConfigureAwait(false);

        if (!force && !status.UpdateAvailable)
        {
            if (status.LatestVersion is null)
            {
                return "could not reach GitHub, so there's nothing to compare against";
            }

            return $"already on {status.InstalledVersion ?? status.LatestVersion}";
        }

        var tag = await _installer.InstallAsync(engine, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Updated {Engine} to {Version}", engine.Descriptor.DisplayName, tag ?? "the latest release");
        return $"updated to {tag ?? "the latest release"}";
    }

    /// <summary>
    /// Runs the update check for every engine that is installed and has auto-update
    /// turned on. One engine failing doesn't stop the others.
    /// </summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One line per engine describing what happened.</returns>
    public async Task<IReadOnlyDictionary<string, string>> RunAutoUpdatesAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var engines = _registry.All;
        var autoUpdate = Plugin.Instance!.Configuration.AutoUpdateEngines;

        for (var i = 0; i < engines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var engine = engines[i];
            var settings = Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id);

            if (!autoUpdate)
            {
                results[engine.Descriptor.Id] = "auto-update off";
            }
            else if (!File.Exists(_runner.ResolvePath(engine)))
            {
                results[engine.Descriptor.Id] = "not installed";
            }
            else if (!string.IsNullOrWhiteSpace(settings.PathOverride))
            {
                // a hand built binary behind a path override isn't ours to replace
                results[engine.Descriptor.Id] = "skipped, using a custom binary path";
            }
            else
            {
                try
                {
                    results[engine.Descriptor.Id] = await UpdateAsync(engine, force: false, cancellationToken).ConfigureAwait(false);
                }

                // Deliberately broad, short of cancellation. One engine that cannot be
                // written (a read-only volume, a permissions problem, a half-extracted
                // archive) used to throw straight out of the loop and take every engine
                // after it down with it, so a single bad install quietly stopped the
                // nightly update for everything else.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Auto-update failed for {Engine}", engine.Descriptor.DisplayName);
                    results[engine.Descriptor.Id] = "failed: " + ex.Message;
                }
            }

            progress?.Report((i + 1) * 100.0 / engines.Count);
        }

        return results;
    }
}
