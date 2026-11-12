// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Tells ntfy and/or Discord when a bulk run is done. One that doesn't get through is
/// logged and that's all, the run is over by then either way.
/// </summary>
public class RunNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RunNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunNotifier"/> class.
    /// </summary>
    public RunNotifier(IHttpClientFactory httpClientFactory, ILogger<RunNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Says whether a notification URL can be saved. ntfy is often self hosted on the LAN so
    /// any http(s) address goes, but the Discord box only takes a Discord webhook.
    /// </summary>
    public static bool IsAllowedUrl(string url, bool discord)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!discord)
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Puts a run into one line.
    /// </summary>
    public static string Describe(BulkRun run)
    {
        var took = run.FinishedUtc - run.StartedUtc;
        var duration = took.TotalHours >= 1 ? $"{(int)took.TotalHours}h {took.Minutes}m"
            : took.TotalMinutes >= 1 ? $"{took.Minutes}m {took.Seconds}s"
            : $"{took.Seconds}s";

        return $"LAPSE: {run.Name} is done. {run.Synced} synced, {run.Unsure} unsure, {run.Failed} failed, {run.Skipped} skipped, took {duration}."
            + (run.Stopped ? " It was stopped by hand." : string.Empty);
    }

    /// <summary>
    /// Sends the run to whichever of the two is set. Never throws.
    /// </summary>
    public async Task NotifyAsync(BulkRun run)
    {
        var config = Plugin.Instance!.Configuration;
        var text = Describe(run);

        if (config.NtfyUrl is { } ntfy)
        {
            using var body = new StringContent(text);
            await PostAsync("ntfy", ntfy, body).ConfigureAwait(false);
        }

        if (config.DiscordWebhookUrl is { } discord)
        {
            // JsonContent has no length and would go out chunked
            using var body = new StringContent(JsonSerializer.Serialize(new { content = text }), Encoding.UTF8, "application/json");
            await PostAsync("Discord", discord, body).ConfigureAwait(false);
        }
    }

    private async Task PostAsync(string target, string url, HttpContent body)
    {
        // the URL is the secret, so only its host goes in the log
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(not a URL)";

        try
        {
            using var response = await _httpClientFactory.CreateClient("LapseNotify")
                .PostAsync(url, body, CancellationToken.None)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Target} at {Host} turned the run notification down with {Status}", target, host, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not send the run notification to {Target} at {Host}: {Error}", target, host, ex.Message);
        }
    }
}
