// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Writes readable (dyslexia friendly) copies of subtitles and puts them back.
/// Libraries are shared, so by default it adds a second track instead of replacing. And
/// replacing always keeps a backup, so "back to normal" always works.
/// </summary>
public class ReadableSubtitleService
{
    /// <summary>
    /// Name tag for readable copies, e.g. Movie.da.readable.ass. Jellyfin still reads the language from it.
    /// </summary>
    public const string ReadableTag = ".readable";

    /// <summary>
    /// Extension for the backed up original. Not a subtitle extension, so Jellyfin won't list it as a track.
    /// </summary>
    public const string BackupExtension = ".lapsebak";

    private readonly SubtitleConverter _converter;
    private readonly FontInstaller _fontInstaller;
    private readonly ILogger<ReadableSubtitleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadableSubtitleService"/> class.
    /// </summary>
    public ReadableSubtitleService(
        SubtitleConverter converter,
        FontInstaller fontInstaller,
        ILogger<ReadableSubtitleService> logger)
    {
        _converter = converter;
        _fontInstaller = fontInstaller;
        _logger = logger;
    }

    /// <summary>
    /// Builds the style from the settings (before it gets fitted to the subtitle's script).
    /// </summary>
    public static SubtitleStyle GetConfiguredStyle()
    {
        var config = Plugin.Instance!.Configuration;

        return new SubtitleStyle
        {
            FontName = config.SubtitleFontName,
            FontSize = config.SubtitleFontSize,
            LetterSpacing = config.SubtitleLetterSpacing,
            Bold = config.SubtitleBold,
            Outline = config.SubtitleOutline,
            MarginV = config.SubtitleMarginV
        };
    }

    /// <summary>
    /// Says whether we made this file readable. Copies have the name tag, replacements have
    /// a marker in the header instead.
    /// </summary>
    public static bool IsReadable(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (Path.GetFileNameWithoutExtension(path).EndsWith(ReadableTag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(Path.GetExtension(path), ".ass", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // the marker is in [Script Info] right at the top, a few lines is enough
            using var reader = new StreamReader(path);

            for (var i = 0; i < 10; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith(SubtitleConverter.ReadableMarker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // can't tell, and this only decides whether to show a button
            return false;
        }

        return false;
    }

    /// <summary>
    /// Writes a readable version of one subtitle.
    /// </summary>
    public async Task<RestyleResult> MakeReadableAsync(
        BaseItem item,
        string sourcePath,
        bool replaceOriginal,
        bool wasEmbedded,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var replace = replaceOriginal && !wasEmbedded;
        var destination = BuildDestination(sourcePath, replace);

        var result = new RestyleResult
        {
            SourcePath = sourcePath,
            OutputPath = destination,
            Replaced = replace
        };

        // A replacement takes the original's name as .ass, which may already exist (say
        // Movie.en.srt next to a Movie.en.ass from Convert). Writing would wreck that track,
        // and renaming would break revert, so just refuse.
        if (replace
            && !string.Equals(sourcePath, destination, StringComparison.Ordinal)
            && File.Exists(destination))
        {
            result.Error = "There is already a subtitle called "
                + Path.GetFileName(destination)
                + " beside this one, and replacing would overwrite it. Write a readable copy instead, or make that subtitle readable rather than this one.";
            return result;
        }

        // backup first, so a failed write can't leave you with neither file
        if (replace)
        {
            var backup = sourcePath + BackupExtension;

            try
            {
                // an existing backup holds the real original from an earlier pass, keep it
                if (!File.Exists(backup))
                {
                    File.Copy(sourcePath, backup);
                }

                result.BackupPath = backup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Error = "Could not back the original up, so nothing was changed: " + ex.Message;
                return result;
            }
        }

        try
        {
            var written = await _converter
                .ConvertStyledAsync(sourcePath, destination, GetConfiguredStyle(), config.SubtitleNonLatinFontName, cancellationToken)
                .ConfigureAwait(false);

            result.Cues = written.Cues;
            result.Script = SubtitleScripts.GetName(written.Script);
            result.FontName = written.Style?.FontName;
            result.LetterSpacing = written.Style?.LetterSpacing ?? 0;
            result.RightToLeft = written.Style?.Encoding > SubtitleScripts.AutoEncoding;
            result.FontSwapped = written.Style is not null
                && !string.Equals(written.Style.FontName, config.SubtitleFontName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            result.Error = ex.Message;
            return result;
        }

        // only now remove the original (nothing to remove if it was .ass replaced in place)
        if (replace && !string.Equals(sourcePath, destination, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the styled file is there, which is what was asked for, just mention it
                result.Error = "Wrote the readable subtitle, but could not remove the original: " + ex.Message;
            }
        }

        result.FontAvailable = IsFontAvailable(result.FontName);
        result.Success = true;

        Record(item, result);

        _logger.LogInformation(
            "Wrote a readable subtitle to {Destination} ({Script}, {Font})",
            destination,
            result.Script,
            result.FontName);

        return result;
    }

    /// <summary>
    /// Puts a subtitle back the way it was before it was made readable: a copy is deleted,
    /// and a replaced original is restored from its backup.
    /// </summary>
    public RestyleRevertItem Revert(string path)
    {
        var reverted = new RestyleRevertItem { Path = path };
        var backup = FindBackup(path);

        try
        {
            if (backup is not null)
            {
                var original = backup[..^BackupExtension.Length];
                File.Copy(backup, original, overwrite: true);
                File.Delete(backup);

                // unless the styled file IS the one we just restored (.ass overwritten in place)
                if (!string.Equals(original, path, StringComparison.Ordinal) && File.Exists(path))
                {
                    File.Delete(path);
                }

                reverted.Success = true;
                reverted.RestoredPath = original;
                _logger.LogInformation("Put {Path} back from its backup", original);
                return reverted;
            }

            if (!File.Exists(path))
            {
                reverted.Error = "That file has already gone.";
                return reverted;
            }

            // no backup = it was an extra track, just delete it
            File.Delete(path);
            reverted.Success = true;
            _logger.LogInformation("Removed the readable subtitle {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reverted.Error = ex.Message;
        }

        return reverted;
    }

    /// <summary>
    /// Finds every readable subtitle on an item, including replaced ones found via their backup.
    /// </summary>
    public static List<string> FindReadable(BaseItem item, IEnumerable<SubtitleOption> subtitles)
    {
        var found = new List<string>();

        foreach (var subtitle in subtitles)
        {
            if (!subtitle.IsEmbedded && IsReadable(subtitle.Path))
            {
                found.Add(subtitle.Path);
            }
        }

        // a replaced subtitle might not be rescanned yet, but its backup names it
        foreach (var backup in EnumerateBackups(item))
        {
            var styled = Path.ChangeExtension(backup[..^BackupExtension.Length], ".ass");

            if (File.Exists(styled) && !found.Contains(styled, StringComparer.Ordinal))
            {
                found.Add(styled);
            }
        }

        return found;
    }

    private static IEnumerable<string> EnumerateBackups(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return Array.Empty<string>();
        }

        var folder = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(folder, stem + "*" + BackupExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // The backup keeps the original's full name, so Movie.da.ass could come from
    // Movie.da.srt.lapsebak just as well as Movie.da.ass.lapsebak.
    private static string? FindBackup(string path)
    {
        var sameName = path + BackupExtension;
        if (File.Exists(sameName))
        {
            return sameName;
        }

        var folder = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem))
        {
            return null;
        }

        foreach (var extension in SubtitleFormats.NativeExtensions)
        {
            var candidate = Path.Combine(folder, stem + extension + BackupExtension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var extension in SubtitleFormats.ConvertibleExtensions)
        {
            var candidate = Path.Combine(folder, stem + extension + BackupExtension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // Always .ass, it's the only format we write that can carry a font. Copies get the
    // readable tag, replacements take the original's name.
    private static string BuildDestination(string sourcePath, bool replace)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        // don't stack the tag when restyling a restyled file
        if (stem.EndsWith(ReadableTag, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^ReadableTag.Length];
        }

        // Movie.en.srt and Movie.en.ass would both become Movie.en.readable.ass and one
        // would overwrite the other, so keep the extension in the name when there's a twin.
        // Copies only: replacements need the plain name for revert to find them (that
        // collision gets refused in MakeReadableAsync instead).
        if (!replace && SharesStemWithAnotherSubtitle(directory, sourcePath, stem))
        {
            stem = Path.GetFileName(sourcePath);
        }

        return Path.Combine(directory, replace ? stem + ".ass" : stem + ReadableTag + ".ass");
    }

    // Checks the actual files, not what this run is restyling, so the name is the same
    // whether one or both twins were ticked.
    private static bool SharesStemWithAnotherSubtitle(string directory, string sourcePath, string stem)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            foreach (var sibling in Directory.EnumerateFiles(directory, stem + ".*"))
            {
                if (string.Equals(sibling, sourcePath, StringComparison.Ordinal)
                    || !SubtitleFormats.IsSubtitle(sibling))
                {
                    continue;
                }

                // Movie.en.readable.ass reduces to Movie.en.readable, that's our own output, not a twin
                if (string.Equals(Path.GetFileNameWithoutExtension(sibling), stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // can't tell, keep the usual name
            return false;
        }

        return false;
    }

    // A missing font still renders, just in some other font. Telling the user means
    // "install the font" instead of "it didn't work".
    private bool IsFontAvailable(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
        {
            return false;
        }

        // we can only check the font we install, assume anything else is on the server
        if (!SubtitleStyle.IsLatinOnlyFont(fontName))
        {
            return true;
        }

        var fonts = _fontInstaller.GetStatus();

        return fonts.FallbackFontEnabled
            && fonts.Fonts.Exists(f => f.StartsWith(fontName, StringComparison.OrdinalIgnoreCase));
    }

    // Into the same history as syncs, so it can be undone from the dashboard too. Deferred
    // save since automation can do lots of these in a row.
    private static void Record(BaseItem item, RestyleResult result)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (Plugin.ConfigurationLock)
        {
            SyncHistoryService.Append(plugin.Configuration, new SyncHistoryEntry
            {
                ItemId = item.Id,
                ItemName = item.Name ?? string.Empty,
                Status = MovieSyncStatus.Synced,
                OutputPath = result.OutputPath,
                InputPath = result.SourcePath,
                BackupPath = result.BackupPath,
                WroteNewFile = !result.Replaced,
                Detail = result.Replaced
                    ? $"Made readable in {result.FontName}, replacing the original"
                    : $"Wrote a readable copy in {result.FontName}"
            });

            plugin.SaveConfigurationSoon();
        }
    }
}
