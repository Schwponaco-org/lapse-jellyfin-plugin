// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Keeps installed engines up to date. The scheduled task and the dashboard button both come through here.
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
    /// Works out what's installed vs what's published.
    /// </summary>
    public async Task<EngineUpdateStatus> CheckAsync(IEngine engine, bool force = false, CancellationToken cancellationToken = default)
    {
        var installedPath = _runner.ResolvePath(engine);
        var installed = File.Exists(installedPath);
        var installedVersion = installed
            ? await ResolveInstalledVersionAsync(engine, cancellationToken).ConfigureAwait(false)
            : null;

        var config = Plugin.Instance!.Configuration;
        var settings = config.FindEngineSettings(engine.Descriptor.Id);

        var status = new EngineUpdateStatus
        {
            EngineId = engine.Descriptor.Id,
            InstalledVersion = installedVersion,
            AutoUpdate = config.AutoUpdateEngines,
            LastCheckedUtc = settings?.LastUpdateCheckUtc
        };

        var latest = await _releaseClient.GetLatestTagAsync(engine.Descriptor.GitHubRepo, force, cancellationToken).ConfigureAwait(false);
        status.LatestVersion = latest;

        // runs for every engine on every page load, only save when the answer changed
        if (latest is not null && !string.Equals(settings?.LatestKnownVersion, latest, StringComparison.Ordinal))
        {
            lock (Plugin.ConfigurationLock)
            {
                settings = config.GetEngineSettings(engine.Descriptor.Id);
                settings.LatestKnownVersion = latest;
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                Plugin.Instance!.SaveConfiguration();
            }

            status.LastCheckedUtc = settings.LastUpdateCheckUtc;
        }

        status.VersionUnknown = installed && installedVersion is null;
        // No updates for a custom binary path (installing writes somewhere else, so the
        // button looked dead). An unknown version counts as out of date, otherwise engines
        // installed while GitHub was down could never be updated from the dashboard.
        status.UpdateAvailable = installed
            && string.IsNullOrWhiteSpace(settings?.PathOverride)
            && engine.Descriptor.GetDownloadForThisMachine() is not null
            && latest is not null
            && (installedVersion is null || GitHubReleaseClient.IsNewer(installedVersion, latest));

        return status;
    }

    /// <summary>
    /// Works out the installed version: the tag recorded at install, or failing that whatever the binary says.
    /// </summary>
    public async Task<string?> ResolveInstalledVersionAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        var recorded = Plugin.Instance!.Configuration.FindEngineSettings(engine.Descriptor.Id)?.InstalledVersion;

        if (!string.IsNullOrWhiteSpace(recorded))
        {
            return recorded;
        }

        var runtime = await _runner.GetRuntimeInfoAsync(engine, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(runtime.Version))
        {
            return null;
        }

        // remember it so we don't have to start the binary again next time
        lock (Plugin.ConfigurationLock)
        {
            Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id).InstalledVersion = runtime.Version;
            Plugin.Instance!.SaveConfiguration();
        }

        return runtime.Version;
    }

    /// <summary>
    /// Replaces an engine's binary with the newest release.
    /// </summary>
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
    /// Updates every installed engine if auto-update is on. One failing doesn't stop the rest.
    /// </summary>
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
            var settings = Plugin.Instance!.Configuration.FindEngineSettings(engine.Descriptor.Id);

            if (!autoUpdate)
            {
                results[engine.Descriptor.Id] = "auto-update off";
            }
            else if (!File.Exists(_runner.ResolvePath(engine)))
            {
                results[engine.Descriptor.Id] = "not installed";
            }
            else if (!string.IsNullOrWhiteSpace(settings?.PathOverride))
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

                // broad on purpose, one broken engine used to stop the nightly update for all the others
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
