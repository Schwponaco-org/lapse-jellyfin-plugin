// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Lapse.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse;

/// <summary>
/// The main LAPSE plugin. Registers the dashboard page and injects the context menu
/// script into the web client, the same way plugins have done this for years (see
/// intro-skipper's old EntryPoint.cs, this plugin followed the exact same approach).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string InjectedScriptTag = "<script src=\"configurationpage?name=lapse-inject.js\"></script>";

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

        var indexPath = Path.Combine(applicationPaths.WebPath, "index.html");
        try
        {
            InjectScript(indexPath, logger);
        }
        catch (Exception ex)
        {
            // Injecting into index.html can fail for all sorts of environment reasons
            // (read only filesystem, wrong file owner, web path missing entirely). None
            // of those should stop the rest of the plugin from loading.
            if (ex is UnauthorizedAccessException)
            {
                logger.LogError(
                    ex,
                    "No permission to modify {Path}. Try fixing its file ownership/permissions (e.g. chown it to the jellyfin user) and restart the server.",
                    indexPath);
            }
            else
            {
                logger.LogError(ex, "Could not inject the LAPSE context menu script into {Path}", indexPath);
            }
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

    private static void InjectScript(string indexPath, ILogger logger)
    {
        if (!File.Exists(indexPath))
        {
            logger.LogWarning("index.html not found at {Path}, skipping script injection", indexPath);
            return;
        }

        var contents = File.ReadAllText(indexPath);

        if (contents.Contains(InjectedScriptTag, StringComparison.Ordinal))
        {
            logger.LogDebug("LAPSE context menu script already injected");
            return;
        }

        var headEndIndex = contents.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEndIndex < 0)
        {
            logger.LogWarning("Could not find </head> in index.html, skipping script injection");
            return;
        }

        contents = contents.Insert(headEndIndex, InjectedScriptTag);
        File.WriteAllText(indexPath, contents);
        logger.LogInformation("LAPSE context menu script injected into the web interface");
    }
}
