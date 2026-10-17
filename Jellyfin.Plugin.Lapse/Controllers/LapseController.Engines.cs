// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Installing, updating and picking sync engines.
/// </summary>
public partial class LapseController
{
    /// <summary>
    /// Gets every engine with its install state, capabilities and release status. The
    /// release check is cached for half an hour so a page load doesn't hit GitHub.
    /// </summary>
    [HttpGet("Lapse/Engines")]
    public async Task<ActionResult<List<EngineInfo>>> GetEngines(CancellationToken cancellationToken)
    {
        // all engines at once, a cold probe starts a few processes per engine
        var defaultId = _registry.GetDefault().Descriptor.Id;
        var infos = await Task.WhenAll(_registry.All.Select(e => DescribeEngineAsync(e, defaultId, cancellationToken))).ConfigureAwait(false);
        return infos.ToList();
    }

    private async Task<EngineInfo> DescribeEngineAsync(IEngine engine, string defaultId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var descriptor = engine.Descriptor;
        var capabilities = descriptor.Capabilities;
        var path = _runner.ResolvePath(engine);
        var installed = System.IO.File.Exists(path);
        var downloadable = EngineRunner.HasDownload(engine);

        var runtimeCall = installed ? _runner.GetRuntimeInfoAsync(engine, cancellationToken) : Task.FromResult(EngineRuntimeInfo.Unknown);
        var vadCall = installed ? _runner.GetVadBackendAsync(engine, cancellationToken) : Task.FromResult<string?>(null);
        var runnableCall = installed ? _runner.CheckRunnableAsync(engine, cancellationToken) : Task.FromResult<string?>(null);
        var statusCall = _updater.CheckAsync(engine, force: false, cancellationToken);

        var runtime = await runtimeCall.ConfigureAwait(false);
        var vadBackend = await vadCall.ConfigureAwait(false);
        var runCheckError = await runnableCall.ConfigureAwait(false);
        var status = await statusCall.ConfigureAwait(false);
        var values = EngineRunner.ResolveParameters(engine);

        var info = new EngineInfo
        {
            Id = descriptor.Id,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            ProjectUrl = descriptor.ProjectUrl,
            BuildGuideUrl = descriptor.BuildGuideUrl,
            Installed = installed,
            Path = path,
            IsDefault = string.Equals(descriptor.Id, defaultId, StringComparison.OrdinalIgnoreCase),
            Tier = descriptor.Tier.ToString(),
            WhyUrl = descriptor.WhyUrl,
            WhyLabel = descriptor.WhyLabel,
            AdvancedNote = descriptor.AdvancedNote,
            DeploymentNote = descriptor.DeploymentNote,
            SubtitleExtensions = installed ? runtime.GetSubtitleExtensions(descriptor.Id) : capabilities.SubtitleExtensions,
            DownloadSupported = downloadable,
            NoDownloadReason = downloadable ? null : EngineInstaller.DescribeMissingBuild(engine),
            RunCheckError = runCheckError,
            SupportsStandard = capabilities.SupportsStandard,
            SupportsAuto = capabilities.SupportsAuto,
            SupportsOls = capabilities.SupportsOls,
            SupportsSplit = capabilities.SupportsSplit,
            SupportsPenalty = capabilities.SupportsPenalty,
            Penalty = EngineRunner.ResolvePenalty(engine, null),
            DefaultPenalty = capabilities.DefaultPenalty,
            MinPenalty = capabilities.MinPenalty,
            MaxPenalty = capabilities.MaxPenalty,
            DefaultMode = EngineRunner.ResolveDefaultMode(engine).ToString(),
            PathOverride = config.FindEngineSettings(descriptor.Id)?.PathOverride,
            InstalledVersion = status.InstalledVersion,
            LatestVersion = status.LatestVersion,
            VersionUnknown = status.VersionUnknown,
            UpdateAvailable = status.UpdateAvailable,
            AutoUpdate = config.AutoUpdateEngines,
            ReportedVersion = runtime.Version,
            DiscoveredFlags = runtime.Probed ? runtime.Flags : null,
            CapabilitySource = runtime.Source,
            VadBackend = vadBackend,
            SupportsOutputFlag = runtime.SupportsOutputFlag,
            SupportsNoBackupFlag = runtime.SupportsNoBackupFlag
        };

        foreach (var mode in descriptor.Modes)
        {
            info.Modes.Add(new EngineModeInfo { Value = mode.Mode.ToString(), Label = mode.Label, Description = mode.Description });
        }

        foreach (var parameter in descriptor.Parameters)
        {
            var entry = new EngineParameterInfo
            {
                Key = parameter.Key,
                Label = parameter.Label,
                Description = parameter.Description,
                Flag = parameter.Flag,
                Kind = parameter.Kind.ToString(),
                DefaultValue = parameter.DefaultValue,
                Value = values.GetString(parameter.Key),
                Minimum = parameter.Minimum,
                Maximum = parameter.Maximum,
                Step = parameter.Step,
                BlankMeansUnset = parameter.BlankMeansUnset
            };

            foreach (var option in parameter.Options)
            {
                entry.Options.Add(new EngineModeInfo { Value = option.Value, Label = option.Label });
            }

            info.Parameters.Add(entry);
        }

        return info;
    }

    /// <summary>
    /// Asks GitHub for the newest release of each engine, skipping the cache.
    /// </summary>
    [HttpGet("Lapse/Engines/Updates")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<List<EngineUpdateStatus>>> CheckEngineUpdates(CancellationToken cancellationToken)
    {
        var result = new List<EngineUpdateStatus>();

        foreach (var engine in _registry.All)
        {
            result.Add(await _updater.CheckAsync(engine, force: true, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Downloads and installs one engine.
    /// </summary>
    [HttpPost("Lapse/Engines/{engineId}/Install")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> InstallEngine([FromRoute] string engineId)
    {
        if (_registry.Find(engineId) is not { } engine)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        try
        {
            var version = await _installer.InstallAsync(engine).ConfigureAwait(false);
            return Ok(new { Version = version });
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Removes our installed copy of an engine. Refuses when it's pointed at a custom
    /// binary, that's not ours to delete.
    /// </summary>
    [HttpPost("Lapse/Engines/{engineId}/Uninstall")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult UninstallEngine([FromRoute] string engineId)
    {
        if (_registry.Find(engineId) is not { } engine)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        if (!string.IsNullOrWhiteSpace(Plugin.Instance!.Configuration.FindEngineSettings(engine.Descriptor.Id)?.PathOverride))
        {
            return BadRequest(
                "This engine is pointed at your own binary, so there's nothing here to remove. "
                + "Clear the binary path override first if you want to stop using it.");
        }

        // each engine has its own folder under ours, so deleting the folder only takes that engine
        var installedPath = _runner.GetInstalledPath(engine);
        var folder = Path.GetDirectoryName(installedPath);

        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            else if (System.IO.File.Exists(installedPath))
            {
                System.IO.File.Delete(installedPath);
            }
            else
            {
                return BadRequest("That engine isn't installed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BadRequest("Could not remove it: " + ex.Message);
        }

        lock (Plugin.ConfigurationLock)
        {
            if (Plugin.Instance.Configuration.FindEngineSettings(engine.Descriptor.Id) is { } settings)
            {
                settings.InstalledVersion = null;
                Plugin.Instance.SaveConfiguration();
            }
        }

        // the probe's cached answer is about a file that's gone now
        _probe.Invalidate();

        return Ok();
    }

    /// <summary>
    /// Updates one engine to the newest release.
    /// </summary>
    [HttpPost("Lapse/Engines/{engineId}/Update")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> UpdateEngine([FromRoute] string engineId, CancellationToken cancellationToken)
    {
        if (_registry.Find(engineId) is not { } engine)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        try
        {
            var outcome = await _updater.UpdateAsync(engine, force: false, cancellationToken).ConfigureAwait(false);
            return Ok(new { Outcome = outcome });
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Turns automatic engine updates on or off for the whole server.
    /// </summary>
    [HttpPost("Lapse/Engines/AutoUpdate")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetEngineAutoUpdate([FromQuery] bool enabled)
    {
        lock (Plugin.ConfigurationLock)
        {
            Plugin.Instance!.Configuration.AutoUpdateEngines = enabled;
            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    /// <summary>
    /// Installs every engine that has a build for this machine, skipping the rest.
    /// </summary>
    [HttpPost("Lapse/Engines/InstallAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> InstallAllEngines()
    {
        var results = new Dictionary<string, string>();

        foreach (var engine in _registry.All)
        {
            if (!EngineRunner.HasDownload(engine))
            {
                results[engine.Descriptor.Id] = "skipped, no build for this machine";
                continue;
            }

            try
            {
                var version = await _installer.InstallAsync(engine).ConfigureAwait(false);
                results[engine.Descriptor.Id] = version is null ? "installed" : "installed " + version;
            }
            catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException)
            {
                results[engine.Descriptor.Id] = "failed: " + ex.Message;
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Sets the engine syncs use when they don't ask for one.
    /// </summary>
    [HttpPost("Lapse/Engines/{engineId}/Default")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetDefaultEngine([FromRoute] string engineId)
    {
        if (_registry.Find(engineId) is not { } engine)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        lock (Plugin.ConfigurationLock)
        {
            Plugin.Instance!.Configuration.DefaultEngineId = engine.Descriptor.Id;
            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    /// <summary>
    /// Sets a single engine parameter, for prompts like "sync even when unsure" that flip
    /// one switch without sending the whole settings page.
    /// </summary>
    [HttpPost("Lapse/Engines/{engineId}/Parameter")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetEngineParameter([FromRoute] string engineId, [FromBody] EngineParameterRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Key))
        {
            return BadRequest("A parameter key is required");
        }

        if (_registry.Find(engineId) is not { } engine)
        {
            return NotFound($"No engine called '{engineId}'");
        }

        // same rule as the settings page, only keys the engine actually has
        if (!engine.Descriptor.Parameters.Exists(p => string.Equals(p.Key, request.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest($"{engine.Descriptor.DisplayName} has no parameter called '{request.Key}'");
        }

        lock (Plugin.ConfigurationLock)
        {
            var settings = Plugin.Instance!.Configuration.GetEngineSettings(engine.Descriptor.Id);
            var existing = settings.Parameters.Find(p => string.Equals(p.Key, request.Key, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                settings.Parameters.Add(new EngineParameterSetting { Key = request.Key, Value = request.Value?.Trim() });
            }
            else
            {
                existing.Value = request.Value?.Trim();
            }

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }
}
