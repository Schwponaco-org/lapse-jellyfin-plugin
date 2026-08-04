// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engine;

/// <summary>
/// Downloads the LAPSE engine binary from the GitHub releases page.
/// </summary>
public class EngineDownloadService
{
    private const string ReleaseBaseUrl = "https://github.com/rs-jensen/lapse/releases/latest/download/";

    private readonly LapseEngineClient _engineClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EngineDownloadService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineDownloadService"/> class.
    /// </summary>
    /// <param name="engineClient">Used to find where the binary should live.</param>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public EngineDownloadService(LapseEngineClient engineClient, IHttpClientFactory httpClientFactory, ILogger<EngineDownloadService> logger)
    {
        _engineClient = engineClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a Linux binary is published for the OS this
    /// server is running on. Only Linux amd64/arm64 builds exist right now.
    /// </summary>
    public bool IsDownloadSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Gets a short description of the detected OS and CPU architecture, for the dashboard.
    /// </summary>
    public string DetectedOsArch => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Builds the current engine status for the dashboard's download section. Actually tries
    /// to start the binary rather than just checking the file exists - a downloaded binary
    /// that's missing a shared library still counts as "not ready" as far as the dashboard
    /// is concerned.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current status.</returns>
    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = _engineClient.ResolveEnginePath();
        var downloaded = File.Exists(path);

        return new EngineStatus
        {
            Downloaded = downloaded,
            Path = path,
            OsArch = DetectedOsArch,
            DownloadSupported = IsDownloadSupported,
            RunCheckError = downloaded ? await _engineClient.CheckRunnableAsync(cancellationToken).ConfigureAwait(false) : null
        };
    }

    /// <summary>
    /// Downloads the right engine binary for this server's architecture and makes it executable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    /// <exception cref="NotSupportedException">Thrown when there's no published binary for this OS.</exception>
    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException(
                $"LAPSE only publishes Linux binaries, but this server is running {DetectedOsArch}. Build the engine yourself and use the binary path override instead.");
        }

        var assetName = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "lapse-linux-arm64"
            : "lapse-linux-amd64";

        var downloadUrl = ReleaseBaseUrl + assetName;
        var targetPath = _engineClient.DefaultEnginePath;

        Directory.CreateDirectory(_engineClient.EngineFolder);

        _logger.LogInformation("Downloading LAPSE engine from {Url}", downloadUrl);

        var client = _httpClientFactory.CreateClient("Lapse");
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(
                $"GitHub returned 404 for {downloadUrl}. That usually means rs-jensen/lapse doesn't have a published release with a '{assetName}' asset yet. " +
                "Build the engine yourself and use the binary path override instead.");
        }

        response.EnsureSuccessStatusCode();

        // Download to a temp file first so a half-finished download never looks like a real binary.
        // If anything goes wrong partway through, clean the temp file up instead of leaving it
        // there to confuse the next attempt (or trip over it with a weird "file not found" error
        // if something upstream fails before the file even gets written).
        var tempPath = targetPath + ".download";
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(tempPath).Length == 0)
            {
                throw new IOException($"Downloaded file from {downloadUrl} was empty");
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        // Linux needs the exec bit set before it'll run the binary directly.
        File.SetUnixFileMode(
            targetPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _logger.LogInformation("LAPSE engine downloaded to {Path}", targetPath);
    }
}
