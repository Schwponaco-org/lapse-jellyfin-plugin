// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Text.Json;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Web;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// What reaches the plugin over the web: the script tag added to the web client's page,
/// and the notifications Radarr and Sonarr send.
/// </summary>
public class WebTests
{
    [Theory]
    [InlineData("/web/", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web", false)]
    [InlineData("/web/main.js", false)]
    [InlineData(null, false)]
    public void OnlyTheWebClientsOwnPageIsRewritten(string? path, bool expected)
    {
        Assert.Equal(expected, WebClientInjection.IsWebIndexPath(path));
    }

    [Theory]
    [InlineData("/web/", "/web")]
    [InlineData("/jellyfin/web/index.html", "/jellyfin/web")]
    public void TheTagsAreRootedWhereTheClientIsServed(string path, string expected)
    {
        Assert.Equal(expected, WebClientInjection.GetWebBasePath(path));
    }

    [Fact]
    public void TheScriptGoesIntoTheHeadOnceWithItsVersion()
    {
        var page = "<html><head><title>Jellyfin</title></head><body></body></html>";

        var injected = WebClientInjection.Inject(page, "/web")!;

        Assert.Contains("<script src=\"/web/configurationpage?name=lapse-inject.js&amp;v=", injected, StringComparison.Ordinal);
        Assert.True(injected.IndexOf("lapse-inject.js", StringComparison.Ordinal) < injected.IndexOf("</head>", StringComparison.Ordinal));

        // Already there, so nothing to do.
        Assert.Null(WebClientInjection.Inject(injected, "/web"));

        // No head to put it in.
        Assert.Null(WebClientInjection.Inject("<html><body></body></html>", "/web"));
    }

    [Fact]
    public void AnOldUnversionedTagIsSwappedForTheCurrentOne()
    {
        var page = "<html><head><script src=\"configurationpage?name=lapse-inject.js\"></script></head></html>";

        var injected = WebClientInjection.Inject(page, "/web")!;

        Assert.DoesNotContain("<script src=\"configurationpage?name=lapse-inject.js\">", injected, StringComparison.Ordinal);
        Assert.Contains("lapse-inject.js&amp;v=", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVideoPathsAreFoundWhereverRadarrAndSonarrPutThem()
    {
        using var radarr = JsonDocument.Parse("""
            {"eventType":"Download","movie":{"folderPath":"/movies/Film (2020)"},
             "movieFile":{"path":"/movies/Film (2020)/Film.mkv","relativePath":"Film.mkv"},
             "deletedFiles":[{"path":"/movies/Film (2020)/Film.old.avi"}]}
            """);
        using var sonarr = JsonDocument.Parse("""
            {"EventType":"Download","episodeFiles":[{"path":"/tv/Show/S01E01.mkv"},{"path":"/tv/Show/S01E01.mkv"},{"path":"/tv/Show/S01E02.MP4"}],
             "series":{"path":"/tv/Show"}}
            """);

        Assert.Equal("download", ArrWebhookService.ReadEventType(radarr.RootElement));
        Assert.Equal(new[] { "/movies/Film (2020)/Film.mkv", "/movies/Film (2020)/Film.old.avi" }, ArrWebhookService.ReadPaths(radarr.RootElement));

        Assert.Equal("download", ArrWebhookService.ReadEventType(sonarr.RootElement));
        Assert.Equal(new[] { "/tv/Show/S01E01.mkv", "/tv/Show/S01E02.MP4" }, ArrWebhookService.ReadPaths(sonarr.RootElement));
    }

    [Fact]
    public void ATestNotificationHasNoPathsAndAJunkBodyHasNoEvent()
    {
        using var test = JsonDocument.Parse("{\"eventType\":\"Test\"}");
        using var junk = JsonDocument.Parse("[1,2,3]");

        Assert.Equal("test", ArrWebhookService.ReadEventType(test.RootElement));
        Assert.Empty(ArrWebhookService.ReadPaths(test.RootElement));
        Assert.Null(ArrWebhookService.ReadEventType(junk.RootElement));
    }
}
