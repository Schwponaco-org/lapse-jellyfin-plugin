// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Downloads and installs engine binaries. Some projects publish the executable directly,
/// others wrap it in a tarball, so this handles both.
/// </summary>
public class EngineInstaller
{
    private readonly EngineRunner _runner;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EngineInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineInstaller"/> class.
    /// </summary>
    /// <param name="runner">Used to work out install paths.</param>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public EngineInstaller(EngineRunner runner, IHttpClientFactory httpClientFactory, ILogger<EngineInstaller> logger)
    {
        _runner = runner;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a short description of the OS and CPU this server is running on.
    /// </summary>
    public static string DetectedOsArch => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Downloads an engine and puts it somewhere it can be run from.
    /// </summary>
    /// <param name="engine">The engine to install.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    /// <exception cref="NotSupportedException">Thrown when there's no build for this machine.</exception>
    public async Task InstallAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException(
                $"Engine downloads are Linux only, and this server is running {DetectedOsArch}. Build the engine yourself and set a path override instead.");
        }

        var url = EngineRunner.GetDownloadUrl(engine);
        if (string.IsNullOrEmpty(url))
        {
            throw new NotSupportedException(
                $"{engine.Descriptor.DisplayName} doesn't publish a build for {RuntimeInformation.OSArchitecture}. Build it yourself and set a path override instead.");
        }

        var targetPath = _runner.GetInstalledPath(engine);
        var engineFolder = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(engineFolder);

        _logger.LogInformation("Installing {Engine} from {Url}", engine.Descriptor.DisplayName, url);

        var tempPath = Path.Combine(engineFolder, engine.Descriptor.ExecutableName + ".download");

        try
        {
            await DownloadToAsync(url, tempPath, engine, cancellationToken).ConfigureAwait(false);

            if (engine.Descriptor.Packaging == EnginePackaging.TarGz)
            {
                await ExtractExecutableAsync(tempPath, targetPath, engine, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }

            MakeExecutable(targetPath);
            _logger.LogInformation("{Engine} installed to {Path}", engine.Descriptor.DisplayName, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task DownloadToAsync(string url, string tempPath, IEngine engine, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("Lapse");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(
                $"GitHub returned 404 for {url}. That release asset may have been renamed or removed for {engine.Descriptor.DisplayName}.");
        }

        response.EnsureSuccessStatusCode();

        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        if (new FileInfo(tempPath).Length == 0)
        {
            throw new IOException($"Downloaded file from {url} was empty");
        }
    }

    // The tarballs we deal with have the executable at the root, but don't assume that -
    // walk the whole archive and take the first entry whose name matches what we're after.
    private static async Task ExtractExecutableAsync(string archivePath, string targetPath, IEngine engine, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            var entryName = Path.GetFileName(entry.Name);
            if (!string.Equals(entryName, engine.Descriptor.ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await entry.ExtractToFileAsync(targetPath, overwrite: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new IOException(
            $"Couldn't find '{engine.Descriptor.ExecutableName}' inside the {engine.Descriptor.DisplayName} download.");
    }

    private static void MakeExecutable(string path)
    {
        // InstallAsync already refuses to run anywhere but Linux, but the analyzer can't
        // see that across the method boundary, so check again here.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
