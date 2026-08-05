// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Net.Http.Headers;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Lapse;

/// <summary>
/// Wires up LAPSE's services in Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // GitHub wants a User-Agent on requests, plain HttpClient doesn't send one by default.
        serviceCollection.AddHttpClient("Lapse", c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Plugin-Lapse", "1.0"));
        });

        serviceCollection.AddSingleton<EngineRegistry>();
        serviceCollection.AddSingleton<EngineRunner>();
        serviceCollection.AddSingleton<EngineInstaller>();
        serviceCollection.AddSingleton<SubtitleLocator>();
        serviceCollection.AddSingleton<SubtitleShifter>();
        serviceCollection.AddSingleton<SyncQueueManager>();
        serviceCollection.AddHostedService<AutoSyncHostedService>();
    }
}
