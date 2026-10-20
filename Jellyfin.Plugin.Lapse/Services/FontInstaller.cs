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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Puts a font where Jellyfin's subtitle renderer finds it.
///
/// A style asking for OpenDyslexic only works if the renderer can get the font. Jellyfin
/// has a fallback font folder for that (the web client's ASS renderer and ffmpeg burn-in
/// both use it), but it ships empty and switched off. This downloads the font and turns
/// the setting on. OpenDyslexic is OFL licensed, any other font dropped in by hand works too.
/// </summary>
public class FontInstaller
{
    // Pinned release, not "latest", the asset names keep moving between releases.
    private const string OpenDyslexicUrl =
        "https://github.com/antijingoist/opendyslexic/releases/download/v0.91.12/opendyslexic-0.910.12-rc2-2019.10.17.zip";

    // what we list as a font, whoever put it there
    private static readonly string[] FontExtensions = { ".otf", ".ttf", ".woff2", ".woff" };

    // the zip also has woff/woff2 builds, libass and ffmpeg can't use those
    private static readonly string[] InstallableExtensions = { ".otf", ".ttf" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<FontInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontInstaller"/> class.
    /// </summary>
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
    /// Gets the font folder, what's in it and whether the server actually uses it.
    /// </summary>
    public FontStatus GetStatus()
    {
        var options = _configurationManager.GetEncodingOptions();
        var folder = ResolveFolder(options.FallbackFontPath);

        var status = new FontStatus
        {
            FolderPath = folder,
            FallbackFontEnabled = options.EnableFallbackFont
        };

        try
        {
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Error = $"Could not read the font folder at {folder}: {ex.Message}";
        }

        status.DyslexicInstalled = status.Fonts.Exists(
            f => f.StartsWith("OpenDyslexic", StringComparison.OrdinalIgnoreCase));

        return status;
    }

    /// <summary>
    /// Downloads OpenDyslexic into the fallback font folder and turns the setting on.
    /// </summary>
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

            // ZipArchive needs a seekable stream, and it's only a couple of MB
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

        // with the setting off the font does nothing, and it'd look like the install failed
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

    /// <summary>
    /// Deletes any installed OpenDyslexic font files from the fallback font folder.
    /// </summary>
    public FontStatus UninstallDyslexicFont()
    {
        var options = _configurationManager.GetEncodingOptions();
        var folder = ResolveFolder(options.FallbackFontPath);

        if (Directory.Exists(folder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(file).StartsWith("OpenDyslexic", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failed = GetStatus();
                failed.Error = "Could not remove OpenDyslexic: " + ex.Message;
                return failed;
            }
        }

        return GetStatus();
    }

    // Flattened, the folder is read as a flat list.
    private static int ExtractFonts(Stream archiveStream, string folder)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var written = 0;

        foreach (var entry in archive.Entries)
        {
            // file name only, so "../../etc/passwd" can't escape the folder
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

    // empty until someone sets it, use a folder next to the server's data then
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
    /// Gets or sets a value indicating whether the server uses that folder at all.
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
