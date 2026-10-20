// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse;

/// <summary>
/// The LAPSE plugin. Registers the dashboard page and the web client scripts (which get
/// injected by <see cref="ScriptInjectionMiddleware"/>).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // see SaveConfigurationSoon
    private static readonly TimeSpan DeferredSaveDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<Plugin> _logger;
    private int _deferredSavePending;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;

        // fix up configs from older versions
        Configuration.MigrateLegacySettings();

        try
        {
            WebClientInjection.WebPath = applicationPaths.WebPath;
        }
        catch (NotSupportedException ex)
        {
            WebClientInjection.Problem = "This server doesn't expose a web client folder, so the item context menu can't be extended.";
            logger.LogWarning(ex, "Could not work out where the Jellyfin web client lives");
        }
    }

    /// <inheritdoc />
    public override string Name => "LAPSE";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("486090e1-ca92-46e1-8549-9f6bb914a1d0");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the lock for editing the config's lists. The queue and request threads both add to
    /// them, and serializing while another thread adds throws.
    /// </summary>
    public static object ConfigurationLock { get; } = new();

    /// <inheritdoc />
    public override void SaveConfiguration(PluginConfiguration config)
    {
        lock (ConfigurationLock)
        {
            // takes any pending deferred save with it
            Interlocked.Exchange(ref _deferredSavePending, 0);
            base.SaveConfiguration(config);
        }
    }

    /// <summary>
    /// Saves a few seconds from now so a burst of changes is one write. The config holds a
    /// record per item, so saving after every item in a bulk run rewrote a big file over and
    /// over. Things that must be on disk before answering still call SaveConfiguration.
    /// </summary>
    public void SaveConfigurationSoon()
    {
        if (Interlocked.Exchange(ref _deferredSavePending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(DeferredSaveDelay).ContinueWith(_ => FlushDeferredSave(), TaskScheduler.Default);
    }

    /// <summary>
    /// Writes out a pending <see cref="SaveConfigurationSoon"/> now. Called when a job finishes.
    /// </summary>
    public void FlushDeferredSave()
    {
        if (Volatile.Read(ref _deferredSavePending) == 0)
        {
            return;
        }

        try
        {
            SaveConfiguration();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // nobody's waiting on this, so just log it
            _logger.LogError(ex, "Could not save the LAPSE configuration");
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html",

                // in the dashboard sidebar instead of 4 clicks deep under My Plugins.
                // Icon is a Material Symbols name.
                EnableInMainMenu = true,
                DisplayName = Name,
                MenuIcon = "subtitles"
            },
            new PluginPageInfo
            {
                Name = "lapse-dashboard.js",
                EmbeddedResourcePath = $"{prefix}.Configuration.lapse-dashboard.js"
            },
            new PluginPageInfo
            {
                Name = "lapse-dashboard.css",
                EmbeddedResourcePath = $"{prefix}.Configuration.lapse-dashboard.css"
            },
            new PluginPageInfo
            {
                Name = "lapse-inject.js",
                EmbeddedResourcePath = $"{prefix}.Configuration.lapse-inject.js"
            },
            new PluginPageInfo
            {
                Name = "lapse-inject.css",
                EmbeddedResourcePath = $"{prefix}.Configuration.lapse-inject.css"
            }
        };
    }
}
