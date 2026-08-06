// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Downloads and installs engine binaries. Projects publish these in all sorts of shapes
/// - a bare executable, a tarball, a zip - and not every project publishes one for every
/// platform, so this handles the packaging differences and gives a straight answer when
/// there simply isn't a build for the machine the server is running on.
/// </summary>
public class EngineInstaller
{
    private readonly EngineRunner _runner;
    private readonly EngineCapabilityProbe _probe;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EngineInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineInstaller"/> class.
    /// </summary>
    /// <param name="runner">Used to work out install paths.</param>
    /// <param name="probe">Cleared after an install, since the binary just changed.</param>
    /// <param name="releaseClient">Used to record which release got installed.</param>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="logger">Logger.</param>
    public EngineInstaller(
        EngineRunner runner,
        EngineCapabilityProbe probe,
        GitHubReleaseClient releaseClient,
        IHttpClientFactory httpClientFactory,
        ILogger<EngineInstaller> logger)
    {
        _runner = runner;
        _probe = probe;
        _releaseClient = releaseClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a short description of the OS and CPU this server is running on.
    /// </summary>
    public static string DetectedOsArch => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Explains why an engine can't be downloaded onto this machine, and what to do about
    /// it. This is the message people actually read when the Install button won't work, so
    /// it spells out the options rather than stopping at "not supported".
    /// </summary>
    /// <param name="engine">The engine with no build for this machine.</param>
    /// <returns>A message to show in the dashboard.</returns>
    public static string DescribeMissingBuild(IEngine engine)
    {
        var descriptor = engine.Descriptor;
        var lines = new List<string>
        {
            $"{descriptor.DisplayName} doesn't publish a build for {DetectedOsArch}."
        };

        if (OperatingSystem.IsWindows())
        {
            lines.Add(
                "On Windows you have three ways forward: run one of the engines that does ship a Windows build "
                + "(alass and ffsubsync both do - install one of those from this page and set it as the default), "
                + "run the engine under WSL or Docker and point the path override at it, "
                + "or build it yourself and point the path override at the .exe.");
        }
        else
        {
            lines.Add("Build it yourself and set a binary path override in Settings, or use one of the other engines.");
        }

        if (!string.IsNullOrWhiteSpace(descriptor.BuildGuideUrl))
        {
            lines.Add($"Build instructions: {descriptor.BuildGuideUrl}");
        }

        return string.Join(" ", lines);
    }

    /// <summary>
    /// Downloads an engine and puts it somewhere it can be run from.
    /// </summary>
    /// <param name="engine">The engine to install.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The release tag that got installed, or null if it couldn't be determined.</returns>
    /// <exception cref="NotSupportedException">Thrown when there's no build for this machine.</exception>
    public async Task<string?> InstallAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        var download = engine.Descriptor.GetDownloadForThisMachine();
        if (download is null)
        {
            throw new NotSupportedException(DescribeMissingBuild(engine));
        }

        var targetPath = _runner.GetInstalledPath(engine);
        var engineFolder = Path.GetDirectoryName(targetPath)!;

        // Before there were several engines, LAPSE's binary was a file sitting directly at
        // engines/lapse. Now that path needs to be the engine's folder, so an old install
        // blocks the new one with "the file already exists". Clear the stale binary out.
        if (File.Exists(engineFolder))
        {
            _logger.LogInformation("Removing old style engine binary at {Path} to make room for the new layout", engineFolder);
            File.Delete(engineFolder);
        }

        Directory.CreateDirectory(engineFolder);

        _logger.LogInformation("Installing {Engine} from {Url}", engine.Descriptor.DisplayName, download.Url);

        var tempPath = Path.Combine(engineFolder, engine.Descriptor.ExecutableName + ".download");

        try
        {
            await DownloadToAsync(download.Url, tempPath, engine, cancellationToken).ConfigureAwait(false);

            switch (download.Packaging)
            {
                case EnginePackaging.TarGz:
                    await ExtractFromTarGzAsync(tempPath, targetPath, engine, cancellationToken).ConfigureAwait(false);
                    break;
                case EnginePackaging.Zip:
                    ExtractFromZip(tempPath, targetPath, engine);
                    break;
                default:
                    File.Move(tempPath, targetPath, overwrite: true);
                    break;
            }

            MakeExecutable(targetPath);
            await DownloadSidecarsAsync(download, engineFolder, engine, cancellationToken).ConfigureAwait(false);
            _probe.Invalidate();
            _logger.LogInformation("{Engine} installed to {Path}", engine.Descriptor.DisplayName, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        // Best effort: the download URLs all point at "latest", so whatever GitHub calls
        // the latest release right now is what just landed on disk. If GitHub can't be
        // reached the install still counts, we just don't know its version.
        var tag = await _releaseClient.GetLatestTagAsync(engine.Descriptor.GitHubRepo, force: true, cancellationToken).ConfigureAwait(false);
        RecordInstalledVersion(engine, tag);
        return tag;
    }

    /// <summary>
    /// Writes down which release of an engine is on disk, so the update check has
    /// something to compare against.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="tag">The release tag, or null if it isn't known.</param>
    public static void RecordInstalledVersion(IEngine engine, string? tag)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var settings = plugin.Configuration.GetEngineSettings(engine.Descriptor.Id);
        settings.InstalledVersion = tag;
        settings.LatestKnownVersion = tag;
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        plugin.SaveConfiguration();
    }

    // Best effort on purpose. Sidecars back an optional capability (LAPSE's Silero VAD
    // falls back to libfvad when its onnxruntime library or model isn't there, the same
    // way it behaves on a machine that never had them), so a missing or renamed sidecar
    // asset should degrade the engine, not fail the whole install.
    private async Task DownloadSidecarsAsync(EngineDownload download, string engineFolder, IEngine engine, CancellationToken cancellationToken)
    {
        foreach (var sidecar in download.Sidecars)
        {
            var sidecarPath = Path.Combine(engineFolder, sidecar.FileName);

            try
            {
                await DownloadToAsync(sidecar.Url, sidecarPath, engine, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Installed {File} alongside {Engine}", sidecar.FileName, engine.Descriptor.DisplayName);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogInformation(
                    ex,
                    "Could not fetch {File} for {Engine}, it will fall back to running without it",
                    sidecar.FileName,
                    engine.Descriptor.DisplayName);

                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
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

    // The archives we deal with have the executable at the root, but don't assume that -
    // walk the whole archive and take the entry that looks most like what we're after.
    private static async Task ExtractFromTarGzAsync(string archivePath, string targetPath, IEngine engine, CancellationToken cancellationToken)
    {
        var names = new List<string>();

        await using (var fileStream = File.OpenRead(archivePath))
        await using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
        await using (var tarReader = new TarReader(gzipStream))
        {
            while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType != TarEntryType.RegularFile)
                {
                    continue;
                }

                names.Add(entry.Name);

                if (IsWantedEntry(Path.GetFileName(entry.Name), engine))
                {
                    await entry.ExtractToFileAsync(targetPath, overwrite: true, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }

        throw new IOException(BuildNotFoundMessage(engine, names));
    }

    private static void ExtractFromZip(string archivePath, string targetPath, IEngine engine)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var entry = archive.Entries.FirstOrDefault(e => IsWantedEntry(e.Name, engine));

        // alass names its Windows build alass-windows64.exe inside the zip rather than
        // alass.exe, so fall back to the only executable in there when the name doesn't
        // line up. Anything with exactly one .exe (or one file at all) is unambiguous.
        entry ??= SingleOrNull(archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        entry ??= SingleOrNull(archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)));

        if (entry is null)
        {
            throw new IOException(BuildNotFoundMessage(engine, archive.Entries.Select(e => e.FullName)));
        }

        entry.ExtractToFile(targetPath, overwrite: true);
    }

    private static ZipArchiveEntry? SingleOrNull(IEnumerable<ZipArchiveEntry> entries)
    {
        var list = entries.ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private static bool IsWantedEntry(string entryName, IEngine engine)
    {
        var wanted = engine.Descriptor.ExecutableName;
        return string.Equals(entryName, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryName, wanted + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNotFoundMessage(IEngine engine, IEnumerable<string> entryNames)
    {
        var names = string.Join(", ", entryNames.Take(10));
        return $"Couldn't find '{engine.Descriptor.ExecutableName}' inside the {engine.Descriptor.DisplayName} download."
            + (string.IsNullOrEmpty(names) ? string.Empty : $" The archive contains: {names}");
    }

    private static void MakeExecutable(string path)
    {
        // Windows has no exec bit, the .exe extension is the whole story there
        if (OperatingSystem.IsWindows())
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
