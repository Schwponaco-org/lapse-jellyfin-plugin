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
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Downloads and installs engine binaries (bare executable, tarball or zip), and says so
/// plainly when there's no build for this machine.
/// </summary>
public sealed class EngineInstaller : IDisposable
{
    // one install at a time, two would share the same temp and staging files
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly EngineRunner _runner;
    private readonly EngineCapabilityProbe _probe;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EngineInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineInstaller"/> class.
    /// </summary>
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
    /// Gets the OS and CPU we're actually running on (read at runtime, so right inside containers too).
    /// </summary>
    public static string DetectedOsArch => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Gets the architecture downloads are picked for. The OS one, not the process one, engines are their own processes.
    /// </summary>
    public static string TargetArchitecture => RuntimeInformation.OSArchitecture.ToString();

    /// <summary>
    /// Gets the process architecture. Differs from <see cref="TargetArchitecture"/> under
    /// emulation, which usually explains an engine that installs fine and won't start.
    /// </summary>
    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>
    /// Explains why there's no download for this machine and what to do instead.
    /// </summary>
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
    /// <exception cref="NotSupportedException">Thrown when there's no build for this machine.</exception>
    public async Task<string?> InstallAsync(IEngine engine, CancellationToken cancellationToken = default)
    {
        var download = engine.Descriptor.GetDownloadForThisMachine();
        if (download is null)
        {
            throw new NotSupportedException(DescribeMissingBuild(engine));
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await InstallLockedAsync(engine, download, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _installGate.Dispose();
    }

    private async Task<string?> InstallLockedAsync(IEngine engine, EngineDownload download, CancellationToken cancellationToken)
    {
        var targetPath = _runner.GetInstalledPath(engine);
        var engineFolder = Path.GetDirectoryName(targetPath)!;

        // old single engine installs put the binary at engines/lapse, which is now the folder
        if (File.Exists(engineFolder))
        {
            _logger.LogInformation("Removing old style engine binary at {Path} to make room for the new layout", engineFolder);
            File.Delete(engineFolder);
        }

        Directory.CreateDirectory(engineFolder);

        // Look up the latest tag first and download that by name, so the recorded version
        // is really what we got. If the API is down the "latest" URL still works.
        var tag = await _releaseClient.GetLatestTagAsync(engine.Descriptor.GitHubRepo, force: true, cancellationToken).ConfigureAwait(false);
        var url = PinToRelease(download.Url, tag);

        _logger.LogInformation("Installing {Engine} {Version} from {Url}", engine.Descriptor.DisplayName, tag ?? "(latest)", url);

        var tempPath = Path.Combine(engineFolder, engine.Descriptor.ExecutableName + ".download");

        try
        {
            await DownloadToAsync(url, tempPath, engine, cancellationToken).ConfigureAwait(false);
            await VerifyChecksumAsync(url, tempPath, engine, cancellationToken).ConfigureAwait(false);

            switch (download.Packaging)
            {
                case EnginePackaging.TarGz:
                    await ExtractFromTarGzAsync(tempPath, targetPath, engineFolder, engine, download, cancellationToken).ConfigureAwait(false);
                    break;
                case EnginePackaging.Zip:
                    ExtractFromZip(tempPath, targetPath, engineFolder, engine, download);
                    break;
                default:
                    MakeExecutable(tempPath);
                    PutInPlace(tempPath, targetPath);
                    break;
            }

            MakeExecutable(targetPath);
            _probe.Invalidate();
            _logger.LogInformation("{Engine} installed to {Path}", engine.Descriptor.DisplayName, targetPath);
        }
        finally
        {
            TryDelete(tempPath);
        }

        RecordInstalledVersion(engine, tag);
        return tag;
    }

    /// <summary>
    /// Turns a /releases/latest/download/ URL into one for a named tag. Anything else is left alone.
    /// </summary>
    public static string PinToRelease(string url, string? tag)
    {
        const string Latest = "/releases/latest/download/";

        if (string.IsNullOrWhiteSpace(tag) || !url.Contains(Latest, StringComparison.Ordinal))
        {
            return url;
        }

        return url.Replace(Latest, "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal);
    }

    // LAPSE releases have a SHA256SUMS file. If there is one the download must match, catches
    // cut off downloads and proxies handing back junk. No file (other engines, old LAPSE) = no check.
    private async Task VerifyChecksumAsync(string url, string archivePath, IEngine engine, CancellationToken cancellationToken)
    {
        var slash = url.LastIndexOf('/');
        if (slash < 0)
        {
            return;
        }

        var assetName = url[(slash + 1)..];
        var sumsUrl = url[..(slash + 1)] + "SHA256SUMS";

        string sums;
        try
        {
            var client = _httpClientFactory.CreateClient("Lapse");
            using var response = await client.GetAsync(sumsUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("No SHA256SUMS published next to {Url}, installing without checking it", url);
                return;
            }

            sums = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Could not fetch SHA256SUMS for {Url}, installing without checking it", url);
            return;
        }

        var expected = FindChecksum(sums, assetName);
        if (expected is null)
        {
            _logger.LogDebug("SHA256SUMS has no line for {Asset}, installing without checking it", assetName);
            return;
        }

        string actual;
        await using (var stream = File.OpenRead(archivePath))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The {engine.Descriptor.DisplayName} download doesn't match the checksum published with the release, "
                + "so it wasn't installed. That's usually a download that got cut short. Try again.");
        }

        _logger.LogInformation("{Asset} matches the published SHA256 checksum", assetName);
    }

    /// <summary>
    /// Finds a file's digest in SHA256SUMS ("digest  name", or "*name" in binary mode).
    /// </summary>
    public static string? FindChecksum(string sums, string assetName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            // 64 hex digits, whitespace, name
            var line = raw.Trim();
            if (line.Length <= 65 || !char.IsWhiteSpace(line[64]))
            {
                continue;
            }

            var name = line[64..].Trim().TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.Ordinal))
            {
                return line[..64];
            }
        }

        return null;
    }

    // Rename over the old file instead of writing into it. A running process keeps the old
    // copy, writing in place gives "text file busy" on Linux and can crash onnxruntime.
    private static void PutInPlace(string staged, string destination)
    {
        try
        {
            File.Move(staged, destination, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Windows won't replace a running exe
            throw new IOException(
                $"Could not replace {Path.GetFileName(destination)}, probably because a sync is using it right now. "
                + "Try again once nothing is syncing.",
                ex);
        }
        finally
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // don't bury the real install error under this
            _logger.LogDebug(ex, "Could not remove {Path}", path);
        }
    }

    private static string Staging(string path)
    {
        return path + ".lapse-new";
    }

    /// <summary>
    /// Records which release is installed so the update check has something to compare to.
    /// </summary>
    public static void RecordInstalledVersion(IEngine engine, string? tag)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (Plugin.ConfigurationLock)
        {
            var settings = plugin.Configuration.GetEngineSettings(engine.Descriptor.Id);
            settings.InstalledVersion = tag;
            settings.LatestKnownVersion = tag;
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            plugin.SaveConfiguration();
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

    // Walk the whole archive for the best matching entry instead of assuming it's at the
    // root. Companion files land next to the executable, that's where the engine looks.
    private static async Task ExtractFromTarGzAsync(
        string archivePath,
        string targetPath,
        string engineFolder,
        IEngine engine,
        EngineDownload download,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var companions = new List<string>();

        // 0 nothing, 1 named like the engine, 2 exact name. Tars are read front to back so
        // a better match later on replaces an earlier one.
        var bestMatch = 0;

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

                var match = IsWantedEntry(name, engine) ? 2 : IsEngineVariant(name, engine) ? 1 : 0;

                if (match > bestMatch)
                {
                    await entry.ExtractToFileAsync(Staging(targetPath), overwrite: true, cancellationToken).ConfigureAwait(false);
                    bestMatch = match;
                    continue;
                }

                if (IsCompanion(name, download))
                {
                    var companionPath = Path.Combine(engineFolder, name);
                    await entry.ExtractToFileAsync(Staging(companionPath), overwrite: true, cancellationToken)
                        .ConfigureAwait(false);
                    companions.Add(companionPath);
                }
            }
        }

        if (bestMatch == 0)
        {
            foreach (var companion in companions)
            {
                File.Delete(Staging(companion));
            }

            throw new IOException(BuildNotFoundMessage(engine, names));
        }

        // only now, so a broken archive leaves the old install alone
        MakeExecutable(Staging(targetPath));
        PutInPlace(Staging(targetPath), targetPath);

        foreach (var companion in companions)
        {
            PutInPlace(Staging(companion), companion);
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

        // alass' Windows zip has alass-cli.exe next to a whole ffmpeg build, so match on the name prefix
        entry ??= archive.Entries.FirstOrDefault(e => IsEngineVariant(e.Name, engine));

        // still nothing, take the only .exe (or the only file) if there's exactly one
        entry ??= SingleOrNull(archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        entry ??= SingleOrNull(archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)));

        if (entry is null)
        {
            throw new IOException(BuildNotFoundMessage(engine, archive.Entries.Select(e => e.FullName)));
        }

        entry.ExtractToFile(Staging(targetPath), overwrite: true);

        var companions = new List<string>();
        foreach (var companion in archive.Entries.Where(e => IsCompanion(e.Name, download)))
        {
            var companionPath = Path.Combine(engineFolder, companion.Name);
            companion.ExtractToFile(Staging(companionPath), overwrite: true);
            companions.Add(companionPath);
        }

        MakeExecutable(Staging(targetPath));
        PutInPlace(Staging(targetPath), targetPath);

        foreach (var companion in companions)
        {
            PutInPlace(Staging(companion), companion);
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

    // alass-cli.exe, alass-windows64: engine name plus - or _. Executables only, so
    // alass-notes.txt or a .dll don't count.
    private static bool IsEngineVariant(string entryName, IEngine engine)
    {
        var extension = Path.GetExtension(entryName);
        if (extension.Length > 0 && !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var wanted = engine.Descriptor.ExecutableName;
        var stem = Path.GetFileNameWithoutExtension(entryName);

        return stem.Length > wanted.Length
            && stem.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
            && (stem[wanted.Length] == '-' || stem[wanted.Length] == '_');
    }

    private static string BuildNotFoundMessage(IEngine engine, IEnumerable<string> entryNames)
    {
        var names = string.Join(", ", entryNames.Take(10));
        return $"Couldn't find '{engine.Descriptor.ExecutableName}' inside the {engine.Descriptor.DisplayName} download."
            + (string.IsNullOrEmpty(names) ? string.Empty : $" The archive contains: {names}");
    }

    private static void MakeExecutable(string path)
    {
        // no exec bit on Windows
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
