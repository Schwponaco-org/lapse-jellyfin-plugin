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
    /// Gets a short description of the OS and CPU this server is running on. Everything
    /// here is read out of the running process, not baked in at build time, so it is what
    /// the server actually is - including inside a container, where the host and the image
    /// can easily disagree.
    /// </summary>
    public static string DetectedOsArch => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Gets the CPU architecture the engine download is chosen for. This is the OS
    /// architecture rather than the process one: engines run as their own processes, so
    /// what matters is what the machine can execute, not how this .NET process happens to
    /// have been started.
    /// </summary>
    public static string TargetArchitecture => RuntimeInformation.OSArchitecture.ToString();

    /// <summary>
    /// Gets the architecture this process is running as. Worth showing next to
    /// <see cref="TargetArchitecture"/> because the two differ under emulation (an x64
    /// build on Apple silicon, an amd64 image on an arm64 host), and when they do, that is
    /// usually the explanation for an engine that installs fine and then won't start.
    /// </summary>
    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

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

        lines.Add(
            "LAPSE itself publishes builds for Linux, macOS and Windows on both Intel and ARM, so the "
            + "simplest way round this is to use LAPSE. Otherwise, build this engine yourself and point "
            + "its binary path override at what you built.");

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
                    await ExtractFromTarGzAsync(tempPath, targetPath, engineFolder, engine, download, cancellationToken).ConfigureAwait(false);
                    break;
                case EnginePackaging.Zip:
                    ExtractFromZip(tempPath, targetPath, engineFolder, engine, download);
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
    // Anything the descriptor lists as a companion comes out at the same time, next to the
    // executable, because that is where the engine looks for it.
    private static async Task ExtractFromTarGzAsync(
        string archivePath,
        string targetPath,
        string engineFolder,
        IEngine engine,
        EngineDownload download,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var foundExecutable = false;

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

                var name = Path.GetFileName(entry.Name);
                names.Add(entry.Name);

                if (!foundExecutable && IsWantedEntry(name, engine))
                {
                    await entry.ExtractToFileAsync(targetPath, overwrite: true, cancellationToken).ConfigureAwait(false);
                    foundExecutable = true;
                    continue;
                }

                if (IsCompanion(name, download))
                {
                    await entry.ExtractToFileAsync(Path.Combine(engineFolder, name), overwrite: true, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (!foundExecutable)
        {
            throw new IOException(BuildNotFoundMessage(engine, names));
        }
    }

    private static void ExtractFromZip(
        string archivePath,
        string targetPath,
        string engineFolder,
        IEngine engine,
        EngineDownload download)
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

        foreach (var companion in archive.Entries.Where(e => IsCompanion(e.Name, download)))
        {
            companion.ExtractToFile(Path.Combine(engineFolder, companion.Name), overwrite: true);
        }
    }

    private static bool IsCompanion(string entryName, EngineDownload download)
    {
        return download.CompanionFiles.Exists(name => string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase));
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
