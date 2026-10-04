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

        // Ask which release is the latest first and download that one by name. The
        // download URLs all say "latest", and asking afterwards could name a release that
        // came out while the archive was downloading. If GitHub's API can't be reached the
        // latest URLs still work, the version just goes unrecorded.
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

        RecordInstalledVersion(engine, tag);
        return tag;
    }

    /// <summary>
    /// Turns a "latest release" download URL into one for a named release, so the file
    /// downloaded is the release that was recorded. URLs in any other shape, and a null
    /// tag, are left as they are.
    /// </summary>
    /// <param name="url">The descriptor's download URL.</param>
    /// <param name="tag">The release tag, or null.</param>
    /// <returns>The URL to download.</returns>
    public static string PinToRelease(string url, string? tag)
    {
        const string Latest = "/releases/latest/download/";

        if (string.IsNullOrWhiteSpace(tag) || !url.Contains(Latest, StringComparison.Ordinal))
        {
            return url;
        }

        return url.Replace(Latest, "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal);
    }

    // LAPSE publishes a SHA256SUMS file with every release. When there is one, the
    // archive has to match it: a download cut short by a flaky connection or a proxy
    // handing back something else is caught here rather than surfacing as an engine that
    // won't start. Releases without the file - the other engines, older LAPSE builds - are
    // installed as before.
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
    /// Finds one file's checksum in a SHA256SUMS file: lines of a hex digest, whitespace,
    /// and the file name, which sha256sum marks with a * in binary mode.
    /// </summary>
    /// <param name="sums">The SHA256SUMS contents.</param>
    /// <param name="assetName">The file to look for.</param>
    /// <returns>The hex digest, or null when the file isn't listed.</returns>
    public static string? FindChecksum(string sums, string assetName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            // A SHA-256 digest is 64 hex digits, then whitespace, then the name.
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

    // Puts a freshly extracted file where the engine runs from by renaming over it. A
    // rename leaves a process that already has the old file open running happily on the
    // old copy, where writing into the file in place fails on Linux while it runs ("text
    // file busy") and, for the onnxruntime library, can crash the process that has it
    // loaded. That matters now a bulk run keeps LAPSE running for its whole length.
    private static void PutInPlace(string staged, string destination)
    {
        try
        {
            File.Move(staged, destination, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Windows won't replace an executable that's running.
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

    private static string Staging(string path)
    {
        return path + ".lapse-new";
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
                await DownloadToAsync(sidecar.Url, Staging(sidecarPath), engine, cancellationToken).ConfigureAwait(false);
                PutInPlace(Staging(sidecarPath), sidecarPath);
                _logger.LogInformation("Installed {File} alongside {Engine}", sidecar.FileName, engine.Descriptor.DisplayName);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogInformation(
                    ex,
                    "Could not fetch {File} for {Engine}, it will fall back to running without it",
                    sidecar.FileName,
                    engine.Descriptor.DisplayName);

                if (File.Exists(Staging(sidecarPath)))
                {
                    File.Delete(Staging(sidecarPath));
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
        var companions = new List<string>();

        // 0 = nothing yet, 1 = something named after the engine, 2 = the exact name. A tar
        // is read front to back, so a suffixed name is taken when it turns up and then
        // given way to the real thing if that appears later on.
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

        // Only once the whole archive has come out, so a broken download leaves the
        // working install as it was rather than half replaced.
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

        // alass's Windows zip calls the binary alass-cli.exe and ships a whole ffmpeg
        // build next to it, so neither the exact name nor "the only .exe in here" finds
        // it. A file named after the engine with a suffix is the engine; ffmpeg.exe
        // sitting beside it is not.
        entry ??= archive.Entries.FirstOrDefault(e => IsEngineVariant(e.Name, engine));

        // Still nothing, so fall back to the only executable in there. Anything with
        // exactly one .exe (or one file at all) is unambiguous.
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

    // Named after the engine but not exactly it: alass-cli.exe, alass-windows64. The
    // separator is what keeps this honest - it matches the engine's own build under
    // another name without ever picking up an unrelated binary that happens to start
    // with the same letters. Only real executables qualify, so a stray alass-cli.dll
    // or alass-notes.txt is left where it is.
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
