// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse;

/// <summary>
/// The main LAPSE plugin. Registers the dashboard page and the script files the web
/// client loads. Getting that script in front of the web client is handled by
/// <see cref="ScriptInjectionMiddleware"/> rather than by editing index.html, which is
/// what this used to do and what stopped the context menu from ever appearing on the
/// packaged Linux and macOS builds.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Carry any settings from the single-engine days onto the LAPSE engine entry so
        // upgrading doesn't quietly drop a configured binary path or penalty.
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

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html"
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
