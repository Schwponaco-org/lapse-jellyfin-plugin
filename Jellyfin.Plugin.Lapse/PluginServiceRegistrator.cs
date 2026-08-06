// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Net.Http.Headers;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using Jellyfin.Plugin.Lapse.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
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
        serviceCollection.AddSingleton<EngineCapabilityProbe>();
        serviceCollection.AddSingleton<EngineRunner>();
        serviceCollection.AddSingleton<GitHubReleaseClient>();
        serviceCollection.AddSingleton<EngineInstaller>();
        serviceCollection.AddSingleton<EngineUpdater>();
        serviceCollection.AddSingleton<LibraryService>();
        serviceCollection.AddSingleton<SubtitleLocator>();
        serviceCollection.AddSingleton<SubtitleShifter>();
        serviceCollection.AddSingleton<SyncQueueManager>();

        serviceCollection.AddSingleton<GoogleTranslationProvider>();
        serviceCollection.AddSingleton<LingarrTranslationProvider>();
        serviceCollection.AddSingleton<TranslationService>();

        serviceCollection.AddHostedService<AutoSyncHostedService>();
        serviceCollection.AddHostedService<LibraryScheduleService>();

        serviceCollection.AddSingleton<IScheduledTask, LibrarySyncTask>();
        serviceCollection.AddSingleton<IScheduledTask, EngineUpdateTask>();
    }
}
