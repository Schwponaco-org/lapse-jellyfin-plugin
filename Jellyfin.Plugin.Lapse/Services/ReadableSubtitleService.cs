// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Writes readable copies of subtitles, and puts them back.
///
/// Two rules run through all of it. The first is that a library is usually shared: one
/// person wanting a dyslexia-friendly subtitle is no reason for everyone else to lose the
/// one they had, so the default writes a second track and leaves the original alone.
/// The second is that nothing here is one-way. Replacing a subtitle keeps the original
/// beside it as a backup, so "put it back to normal" is always something the plugin can
/// actually do rather than something the person has to have thought of in advance.
/// </summary>
public class ReadableSubtitleService
{
    /// <summary>
    /// The name tag a readable copy carries, e.g. Movie.da.readable.ass. Jellyfin still
    /// reads the language off the part before it, so the copy lands in the player next to
    /// the subtitle it came from rather than as some untagged extra.
    /// </summary>
    public const string ReadableTag = ".readable";

    /// <summary>
    /// The extension put on the original when a readable version replaces it. Not a
    /// subtitle extension, so Jellyfin doesn't offer the backup as a track of its own.
    /// </summary>
    public const string BackupExtension = ".lapsebak";

    private readonly SubtitleConverter _converter;
    private readonly FontInstaller _fontInstaller;
    private readonly ILogger<ReadableSubtitleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadableSubtitleService"/> class.
    /// </summary>
    /// <param name="converter">Reads the subtitle and writes the styled copy.</param>
    /// <param name="fontInstaller">Says whether the font being asked for is installed.</param>
    /// <param name="logger">Logger.</param>
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
    /// Builds the style from the current settings, before it's fitted to whatever script
    /// the subtitle turns out to be in.
    /// </summary>
    /// <returns>The configured style.</returns>
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
    /// Says whether a file is one the plugin made readable. The name tag answers it for
    /// the copies; the marker in the header answers it for the ones that replaced their
    /// original and so carry an ordinary name.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns>True when this is a readable subtitle.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path is one the library reported for an item.")]
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
            // The marker sits in [Script Info], which is the top of the file. Reading the
            // first few lines is enough and keeps this cheap enough to call per track.
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
            // Unreadable is not the same as "not readable subtitles", but it's the only
            // answer available and the caller only ever uses it to offer a button.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Writes a readable version of one subtitle.
    /// </summary>
    /// <param name="item">The item it belongs to, for the history entry.</param>
    /// <param name="sourcePath">The subtitle file, already extracted to disk if it was an
    /// embedded track.</param>
    /// <param name="replaceOriginal">True to leave one subtitle where there were two, with
    /// the original kept as a backup. False writes a second track and changes nothing.</param>
    /// <param name="wasEmbedded">True when the source was extracted out of the video for
    /// this call. Replacing one of those is meaningless - the track is still in the video -
    /// so it's quietly treated as a copy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was written.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The source is a subtitle the library listed for this item; every destination is derived from it with a fixed extension.")]
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

        // A replacement takes the original's name with an .ass extension, which is already
        // taken when the item carries the same subtitle in two formats - Movie.en.srt next
        // to the Movie.en.ass that Convert left behind. Writing anyway would destroy a
        // track nobody asked about, and back up the wrong file while doing it. The styled
        // file can't be renamed out of the way either, since a revert finds it by that
        // exact name, so this is a case to turn away rather than guess at.
        if (replace
            && !string.Equals(sourcePath, destination, StringComparison.Ordinal)
            && File.Exists(destination))
        {
            result.Error = "There is already a subtitle called "
                + Path.GetFileName(destination)
                + " beside this one, and replacing would overwrite it. Write a readable copy instead, or make that subtitle readable rather than this one.";
            return result;
        }

        // The backup comes first, so a write that fails halfway can't leave someone with
        // neither the original nor a usable styled file.
        if (replace)
        {
            var backup = sourcePath + BackupExtension;

            try
            {
                // An existing backup is from an earlier pass and holds the true original.
                // Overwriting it with an already-styled file would lose that for good.
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

        // Only now that there is a styled file does the original go. An overwrite in place
        // - an .ass replaced by an .ass - has nothing left to remove.
        if (replace && !string.Equals(sourcePath, destination, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The styled file is written, which is what was asked for. A leftover
                // original is worth saying so about rather than failing the whole thing.
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
    /// <param name="path">The readable subtitle file.</param>
    /// <returns>What happened.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path is one the library reported for this item, and the backup is derived from it.")]
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

                // The styled file only goes if it isn't the file we just restored, which it
                // is when an .ass was overwritten in place.
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

            // No backup means nothing was replaced, so the readable version is an extra
            // track and removing it leaves the original exactly as it was.
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
    /// Finds every readable subtitle on an item: the copies the plugin wrote, and any
    /// original still sitting beside one as a backup.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="subtitles">The item's subtitles, as the locator found them.</param>
    /// <returns>The files a revert would act on.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Every path looked at here is one the library reported for this item, or one derived from the item's own video path. Nothing from the request reaches it.")]
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

        // A replaced subtitle whose library entry hasn't been rescanned yet won't be in
        // that list, but its backup is still on disk beside the video and names it.
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The folder and the name it matches on both come from the item's own video path, which Jellyfin resolved.")]
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

    // The original this styled file replaced, whatever format it was in. The backup keeps
    // the original's whole name, so Movie.da.ass may have come from Movie.da.srt.lapsebak
    // as easily as from Movie.da.ass.lapsebak.
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

    // Always .ass: it's the only one of the formats the plugin writes with anywhere to put
    // a font. A copy carries the readable tag so both tracks are offered; a replacement
    // takes the original's name so there's still only one.
    private static string BuildDestination(string sourcePath, bool replace)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        // Restyling something already restyled replaces it rather than stacking the tag.
        if (stem.EndsWith(ReadableTag, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^ReadableTag.Length];
        }

        // Dropping the source's extension is what makes the name readable, but it also
        // makes it ambiguous: Movie.en.srt and Movie.en.ass - exactly what Convert leaves
        // behind - both come out as Movie.en.readable.ass, and ticking both in the dialog
        // means the second quietly overwrites the first. Keeping the extension in the name
        // is the only thing that tells the two apart, so it goes back in when, and only
        // when, there is something to be told apart from.
        //
        // Only for a copy. A replacement has to keep the plain name, because that name is
        // how a revert pairs the styled file back up with the .lapsebak beside it; renaming
        // it here would have the revert delete whatever did hold the plain name instead.
        // The same collision is turned away in MakeReadableAsync rather than renamed.
        if (!replace && SharesStemWithAnotherSubtitle(directory, sourcePath, stem))
        {
            stem = Path.GetFileName(sourcePath);
        }

        return Path.Combine(directory, replace ? stem + ".ass" : stem + ReadableTag + ".ass");
    }

    // Whether another subtitle beside this one would reduce to the same name. Answered off
    // the files rather than off what this run happens to be restyling, so one subtitle
    // gets the same readable name whether it was ticked on its own or alongside its twin.
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

                // Only a name that reduces to the same stem collides. Movie.en.readable.ass
                // sits next to Movie.en.srt but reduces to Movie.en.readable, and it's this
                // subtitle's own output rather than a track competing for the name.
                if (string.Equals(Path.GetFileNameWithoutExtension(sibling), stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell, so keep the name that has always been used.
            return false;
        }

        return false;
    }

    // A style naming a font the renderer can't find still renders - in something else.
    // Saying so is the difference between "it didn't work" and "install the font".
    private bool IsFontAvailable(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
        {
            return false;
        }

        // Anything but the font the plugin installs is one the server already had, or one
        // the admin named themselves, and the fallback folder says nothing about those.
        if (!SubtitleStyle.IsLatinOnlyFont(fontName))
        {
            return true;
        }

        var fonts = _fontInstaller.GetStatus();

        return fonts.FallbackFontEnabled
            && fonts.Fonts.Exists(f => f.StartsWith(fontName, StringComparison.OrdinalIgnoreCase));
    }

    // Goes in the same history the dashboard already lists syncs in, so a readable
    // subtitle can be undone from there as well as from the item's own menu.
    private static void Record(BaseItem item, RestyleResult result)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var history = plugin.Configuration.History;

        history.Add(new SyncHistoryEntry
        {
            ItemId = item.Id,
            ItemName = item.Name ?? string.Empty,
            Status = MovieSyncStatus.Synced,
            OutputPath = result.OutputPath,
            BackupPath = result.BackupPath,
            WroteNewFile = !result.Replaced,
            Detail = result.Replaced
                ? $"Made readable in {result.FontName}, replacing the original"
                : $"Wrote a readable copy in {result.FontName}"
        });

        if (history.Count > PluginConfiguration.MaxHistoryEntries)
        {
            history.RemoveRange(0, history.Count - PluginConfiguration.MaxHistoryEntries);
        }

        plugin.SaveConfiguration();
    }
}
