// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Puts a font where Jellyfin's subtitle renderer will find it.
///
/// Styling a subtitle to use OpenDyslexic only does anything if whatever is drawing the
/// text can get hold of OpenDyslexic. Jellyfin already has the mechanism for that - the
/// fallback font folder, which the web client's ASS renderer fetches fonts from and which
/// ffmpeg reads when it burns subtitles in - but it ships empty, the setting is off by
/// default, and finding a font, downloading it and pointing the server at it is several
/// steps in three different places. This does those steps.
///
/// OpenDyslexic is used because it is free to redistribute (SIL Open Font License) and is
/// the typeface the accessibility guidance names. Nothing here is specific to it: any font
/// dropped in the same folder by hand works the same way, and the style can name it.
/// </summary>
public class FontInstaller
{
    /// <summary>
    /// Where the OpenDyslexic release archive is fetched from. A pinned release rather
    /// than "latest": the asset names have moved between releases, and a font that quietly
    /// stops installing is worse than one that installs a slightly older version.
    /// </summary>
    private const string OpenDyslexicUrl =
        "https://github.com/antijingoist/opendyslexic/releases/download/v0.91.12/opendyslexic-0.910.12-rc2-2019.10.17.zip";

    // What counts as a font when reporting what's in the folder, whoever put it there.
    private static readonly string[] FontExtensions = { ".otf", ".ttf", ".woff2", ".woff" };

    // What gets unpacked. The archive also carries woff and woff2 builds of every face,
    // which are for web pages: libass and ffmpeg read neither, so unpacking them would
    // triple the folder's contents with files nothing here can use.
    private static readonly string[] InstallableExtensions = { ".otf", ".ttf" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<FontInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontInstaller"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory to grab an HttpClient from.</param>
    /// <param name="configurationManager">Reads and writes the server's encoding options,
    /// which is where the fallback font folder is configured.</param>
    /// <param name="logger">Logger.</param>
    public FontInstaller(
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager configurationManager,
        ILogger<FontInstaller> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets where the fonts live and what is in there, along with whether the server is
    /// set to actually use them.
    /// </summary>
    /// <returns>The current state.</returns>
    public FontStatus GetStatus()
    {
        var options = _configurationManager.GetEncodingOptions();
        var folder = ResolveFolder(options.FallbackFontPath);

        var status = new FontStatus
        {
            FolderPath = folder,
            FallbackFontEnabled = options.EnableFallbackFont
        };

        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (FontExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    status.Fonts.Add(Path.GetFileName(file));
                }
            }

            status.Fonts.Sort(StringComparer.OrdinalIgnoreCase);
        }

        status.DyslexicInstalled = status.Fonts.Exists(
            f => f.StartsWith("OpenDyslexic", StringComparison.OrdinalIgnoreCase));

        return status;
    }

    /// <summary>
    /// Downloads OpenDyslexic and unpacks its font files into the fallback font folder,
    /// then turns the fallback font setting on so the server actually serves them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state afterwards, with an error set if it didn't work.</returns>
    public async Task<FontStatus> InstallDyslexicFontAsync(CancellationToken cancellationToken = default)
    {
        var options = _configurationManager.GetEncodingOptions();
        var folder = ResolveFolder(options.FallbackFontPath);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = GetStatus();
            failed.Error = $"Could not create the font folder at {folder}: {ex.Message}";
            return failed;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Lapse");

            using var response = await client
                .GetAsync(OpenDyslexicUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            // Into memory rather than a scratch file: the archive is a couple of megabytes
            // and ZipArchive wants a seekable stream, which the response body is not.
            using var buffer = new MemoryStream();

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;

            var written = ExtractFonts(buffer, folder);

            if (written == 0)
            {
                var empty = GetStatus();
                empty.Error = "The OpenDyslexic download had no font files in it.";
                return empty;
            }

            _logger.LogInformation("Installed {Count} OpenDyslexic font file(s) into {Folder}", written, folder);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not install OpenDyslexic");

            var failed = GetStatus();
            failed.Error = "Could not download OpenDyslexic: " + ex.Message +
                " The font can also be put into the folder by hand.";
            return failed;
        }

        // A font in the folder that the server has been told to ignore does nothing, and
        // "I installed it and nothing changed" is the obvious way for that to be reported.
        try
        {
            if (!options.EnableFallbackFont || !string.Equals(options.FallbackFontPath, folder, StringComparison.Ordinal))
            {
                options.EnableFallbackFont = true;
                options.FallbackFontPath = folder;
                _configurationManager.SaveConfiguration("encoding", options);

                _logger.LogInformation("Turned the server's fallback font setting on, pointing at {Folder}", folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var partial = GetStatus();
            partial.Error = "The font was installed, but the server's fallback font setting could not be turned on: " +
                ex.Message + " Turn it on under Dashboard > Playback.";
            return partial;
        }

        return GetStatus();
    }

    // Flattened on purpose: the archive nests its fonts under a folder or two, and the
    // fallback font folder is read as a flat list of files.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The destination is the server's own configured font folder, and only the file name of each entry is used - never its path within the archive, which is what a zip slip would need.")]
    private static int ExtractFonts(Stream archiveStream, string folder)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var written = 0;

        foreach (var entry in archive.Entries)
        {
            // Take the entry's own file name and nothing else. An entry named
            // "../../etc/passwd" contributes "passwd" and lands in the font folder like
            // everything else.
            var name = Path.GetFileName(entry.FullName);

            if (string.IsNullOrEmpty(name)
                || !InstallableExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(folder, name), overwrite: true);
            written++;
        }

        return written;
    }

    // Jellyfin leaves this empty until someone sets it, so fall back to a folder beside
    // the rest of the server's data rather than refusing to do anything.
    private string ResolveFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(_configurationManager.ApplicationPaths.DataPath, "fonts");
    }
}

/// <summary>
/// What is installed where, for the dashboard's font section.
/// </summary>
public class FontStatus
{
    /// <summary>
    /// Gets or sets the folder Jellyfin reads fonts from.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the server is set to serve fonts from that
    /// folder at all. Off, nothing in the folder is used.
    /// </summary>
    public bool FallbackFontEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an OpenDyslexic file is among them.
    /// </summary>
    public bool DyslexicInstalled { get; set; }

    /// <summary>
    /// Gets the font files in the folder, whoever put them there.
    /// </summary>
    public List<string> Fonts { get; } = new();

    /// <summary>
    /// Gets or sets why the last install didn't work, when it didn't.
    /// </summary>
    public string? Error { get; set; }
}
