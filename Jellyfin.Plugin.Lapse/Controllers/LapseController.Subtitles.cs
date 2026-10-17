// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Lapse.Controllers;

/// <summary>
/// Working on one subtitle: translate, shift, convert, restyle, and pulling tracks out of the video.
/// </summary>
public partial class LapseController
{
    private const string NotThisItemsSubtitle = "That subtitle doesn't belong to this item, or the item doesn't exist";
    private const string CouldNotExtract = "Could not get that subtitle out of the video file.";

    /// <summary>
    /// Translates one of an item's subtitles into a new file next to it. The source isn't touched.
    /// </summary>
    [HttpPost("Lapse/Translate")]
    public async Task<ActionResult<TranslationResult>> Translate(
        [FromBody] TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var (item, match) = FindSubtitle(request.ItemId, request.SubtitlePath);
        if (item is null || match is null)
        {
            return BadRequest(NotThisItemsSubtitle);
        }

        var (path, error) = await _extractor.ResolveAsync(item, match, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return BadRequest(error ?? CouldNotExtract);
        }

        return await _translationService.TranslateAsync(path, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the translation providers in display order. Unconfigured ones come back with the
    /// reason so the dashboard can grey them out.
    /// </summary>
    [HttpGet("Lapse/Translate/Providers")]
    public ActionResult<List<object>> GetTranslationProviders()
    {
        var defaultProvider = Plugin.Instance!.Configuration.DefaultTranslationProvider;

        return _translationService.Providers
            .Select(p =>
            {
                var problem = p.GetConfigurationProblem();
                return (object)new
                {
                    Id = p.Id.ToString(),
                    p.DisplayName,
                    p.Tier,
                    p.Summary,
                    Problem = problem,
                    Configured = problem is null,
                    IsDefault = p.Id == defaultProvider
                };
            })
            .ToList();
    }

    /// <summary>
    /// Gets what the translate dialog starts on, so nobody has to type the language on a TV remote every time.
    /// </summary>
    [HttpGet("Lapse/Translate/Defaults")]
    public ActionResult<TranslationDefaults> GetTranslationDefaults()
    {
        var config = Plugin.Instance!.Configuration;

        return new TranslationDefaults
        {
            // fall back to the auto translate language, probably what they want anyway
            TargetLanguage = Blank(config.TranslationDefaultTargetLanguage) ?? config.AutoTranslateLanguage,
            SourceLanguage = config.TranslationDefaultSourceLanguage,
            Provider = config.DefaultTranslationProvider.ToString(),
            ConfidenceThreshold = config.TranslationConfidenceThreshold,
            IncludeMetadataHeader = config.TranslationIncludeMetadataHeader
        };
    }

    /// <summary>
    /// Nudges a subtitle's timings by hand for when a sync is close but not quite there.
    /// Output follows the configured output mode, same as a sync.
    /// </summary>
    [HttpPost("Lapse/Shift")]
    public async Task<ActionResult<ShiftResult>> Shift(
        [FromBody] ShiftRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            return BadRequest("A subtitle path is required");
        }

        var (item, match) = FindSubtitle(request.ItemId, request.SubtitlePath);
        if (item is null || match is null)
        {
            return BadRequest(NotThisItemsSubtitle);
        }

        var (path, error) = await _extractor.ResolveAsync(item, match, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return BadRequest(error ?? CouldNotExtract);
        }

        try
        {
            var shifted = await _subtitleShifter
                .ShiftAsync(path, request.ResolveOffsetSeconds(), request.OutputMode, cancellationToken)
                .ConfigureAwait(false);

            // still synced after a nudge, don't let the newer write time make it look replaced
            SyncQueueManager.TouchSyncedSubtitle(request.ItemId, shifted.OutputPath);
            return shifted;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Writes readable (dyslexia friendly) ASS copies of the given subtitles. Done in the
    /// file since most clients have no font picker, and a style in the file works on every
    /// device. Adds a second track by default rather than replacing, libraries are shared.
    /// </summary>
    [HttpPost("Lapse/Restyle")]
    public async Task<ActionResult<RestyleResponse>> Restyle(
        [FromBody] RestyleRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var wanted = request.SubtitlePaths is { Count: > 0 } ? request.SubtitlePaths : new List<string>();
        if (wanted.Count == 0 && !string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            wanted.Add(request.SubtitlePath);
        }

        if (wanted.Count == 0)
        {
            return BadRequest("At least one subtitle is required");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        var response = new RestyleResponse();
        var subtitles = _subtitleLocator.GetExternalSubtitles(item);

        foreach (var path in wanted)
        {
            var match = subtitles.Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));
            string? error = null;
            string? sourcePath = null;

            if (match is null)
            {
                error = "That subtitle doesn't belong to this item";
            }
            else if (!match.TextBased)
            {
                error = "That's a picture based subtitle (PGS or VobSub). There are no characters in it to set a font on, it needs OCR first.";
            }
            else
            {
                (sourcePath, error) = await _extractor.ResolveAsync(item, match, cancellationToken).ConfigureAwait(false);
                error = sourcePath is null ? error ?? CouldNotExtract : null;
            }

            if (error is not null || sourcePath is null)
            {
                response.Files.Add(new RestyleResult { SourcePath = path, Error = error });
                continue;
            }

            response.Files.Add(await _readable
                .MakeReadableAsync(item, sourcePath, request.ReplaceOriginal, match!.IsEmbedded, cancellationToken)
                .ConfigureAwait(false));
        }

        response.Success = response.Files.Exists(f => f.Success);
        return response;
    }

    /// <summary>
    /// Undoes <see cref="Restyle"/>: deletes the readable copy, or puts the backup back if it replaced the original.
    /// </summary>
    [HttpPost("Lapse/Restyle/Revert")]
    public ActionResult<RestyleRevertResponse> RevertRestyle([FromBody] RestyleRevertRequest request)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("A request body is required");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        var response = new RestyleRevertResponse();

        var targets = string.IsNullOrWhiteSpace(request.SubtitlePath)
            ? ReadableSubtitleService.FindReadable(item, subtitles)
            : new List<string> { request.SubtitlePath };

        foreach (var target in targets)
        {
            // must belong to this item. A file we just wrote may not be scanned yet, so
            // anything next to the video named after it counts too
            var known = subtitles.Exists(s => string.Equals(s.Path, target, StringComparison.Ordinal))
                || IsBesideItem(item, target);

            response.Files.Add(known
                ? _readable.Revert(target)
                : new RestyleRevertItem { Path = target, Error = "That subtitle doesn't belong to this item" });
        }

        response.Success = response.Files.Exists(f => f.Success);
        return response;
    }

    // Same folder as the video and named after it, like everything we write.
    private static bool IsBesideItem(BaseItem item, string path)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);

        return folder is not null
            && string.Equals(Path.GetDirectoryName(path), folder, StringComparison.Ordinal)
            && Path.GetFileName(path).StartsWith(stem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes every text track in the video out as a file beside it, and optionally remuxes
    /// the video without them (helps direct play). Never called by anything automatic.
    /// </summary>
    [HttpPost("Lapse/ExtractEmbedded")]
    public async Task<ActionResult<RemuxResult>> ExtractEmbedded(
        [FromBody] ExtractEmbeddedRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("Nothing to do");
        }

        // rewriting the video itself stays admin only whatever the access setting says
        if (request.RemoveFromVideo && !User.IsInRole(AdministratorRole))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only administrators can remove tracks from a video file.");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        return await _remuxer
            .ExtractEmbeddedSubtitlesAsync(item, request.RemoveFromVideo, request.ReplaceOriginal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a subtitle out in another format without syncing it. Also the way in for
    /// formats no engine reads, e.g. convert a MicroDVD .sub to srt and go from there.
    /// </summary>
    [HttpPost("Lapse/Convert")]
    public async Task<ActionResult<ConvertResult>> ConvertSubtitle(
        [FromBody] ConvertRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SubtitlePath))
        {
            return BadRequest("A subtitle path is required");
        }

        var config = Plugin.Instance!.Configuration;

        // empty means whatever the settings say
        if (!SubtitleFormats.TryNormalizeOutputFormat(Blank(request.TargetFormat) ?? config.ConversionFormat, out var target))
        {
            return BadRequest("The target format has to be srt, vtt, ass or ssa");
        }

        var (item, match) = FindSubtitle(request.ItemId, request.SubtitlePath);
        if (item is null || match is null)
        {
            return BadRequest(NotThisItemsSubtitle);
        }

        var (sourcePath, sourceError) = await _extractor.ResolveAsync(item, match, cancellationToken).ConfigureAwait(false);
        if (sourcePath is null)
        {
            return BadRequest(sourceError ?? CouldNotExtract);
        }

        var sourceFormat = SubtitleFormats.GetName(sourcePath);
        if (string.Equals(sourceFormat, target, StringComparison.Ordinal))
        {
            return BadRequest($"That subtitle is already {target}.");
        }

        var destination = UniqueConversionPath(sourcePath, target);

        try
        {
            var cues = await _converter.ConvertAsync(sourcePath, destination, cancellationToken: cancellationToken).ConfigureAwait(false);

            var result = new ConvertResult
            {
                OutputPath = destination,
                Format = target,
                SourceFormat = sourceFormat,
                Cues = cues,

                // an embedded track was just extracted to get here, there's no original file to remove
                RemovedOriginal = (request.ReplaceOriginal ?? config.ConversionReplaceOriginal)
                    && !match.IsEmbedded
                    && TryDeleteFile(sourcePath)
            };

            // converting is usually a step towards syncing, so carry on unless told not to
            if ((request.SyncAfter ?? config.ConversionSyncAfter) && !string.IsNullOrEmpty(item.Path))
            {
                var engine = _registry.Resolve(request.EngineId);

                result.Sync = await _runner
                    .RunAsync(
                        engine,
                        item.Path,
                        destination,
                        EngineRunner.ResolveDefaultMode(engine),
                        EngineRunner.ResolvePenalty(engine, null),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                result.SyncedAfter = true;
                SaveSyncRecord(request.ItemId, result.Sync, destination);
            }

            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Runs convert, sync and translate on one subtitle in one go. Fixed order since it's the
    /// only one that works, and server side because each step needs the file the last one made.
    /// </summary>
    [HttpPost("Lapse/Pipeline")]
    public async Task<ActionResult<PipelineResult>> RunPipeline(
        [FromBody] PipelineRequest request,
        CancellationToken cancellationToken)
    {
        if (CheckSubtitleAccess() is { } denied)
        {
            return denied;
        }

        if (request is null)
        {
            return BadRequest("Nothing to do");
        }

        var item = GetVisibleItem(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found, or it has no video file");
        }

        var subtitles = _subtitleLocator.GetExternalSubtitles(item);
        var picked = string.IsNullOrWhiteSpace(request.SubtitlePath)
            ? subtitles.Find(s => s.Supported)
            : subtitles.Find(s => string.Equals(s.Path, request.SubtitlePath, StringComparison.Ordinal));

        if (picked is null)
        {
            return BadRequest("That subtitle doesn't belong to this item");
        }

        if (!picked.Supported)
        {
            return BadRequest("That's a picture based subtitle (PGS or VobSub). There's no text in it to work with, it needs OCR first.");
        }

        string? outputFormat = null;
        if (request.OutputFormat is not null && !SubtitleFormats.TryNormalizeOutputFormat(request.OutputFormat, out outputFormat))
        {
            return BadRequest("The output format has to be srt, vtt, ass or ssa");
        }

        var result = new PipelineResult { Extracted = picked.IsEmbedded };

        var (path, resolveError) = await _extractor.ResolveAsync(item, picked, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            result.Error = resolveError ?? CouldNotExtract;
            return result;
        }

        result.SubtitlePath = path;

        if (request.Sync)
        {
            var engine = _registry.Resolve(request.EngineId);
            var mode = request.Mode ?? EngineRunner.ResolveDefaultMode(engine);

            if (!SupportsMode(engine, mode))
            {
                result.Error = $"{engine.Descriptor.DisplayName} doesn't support {mode} alignment";
                return result;
            }

            var sync = await _runner
                .RunAsync(
                    engine,
                    item.Path,
                    path,
                    mode,
                    EngineRunner.ResolvePenalty(engine, request.Penalty),
                    request.OutputMode,
                    outputFormat: outputFormat,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            result.Sync = sync;
            result.ConvertedFrom = sync.ConvertedFrom;
            SaveSyncRecord(request.ItemId, sync, path);

            if (!sync.Success)
            {
                result.Error = sync.Error;
                return result;
            }

            // from here on work on what the sync wrote
            if (!string.IsNullOrEmpty(sync.OutputPath))
            {
                result.SubtitlePath = sync.OutputPath;
            }
        }
        else if (outputFormat is not null
            && !string.Equals(SubtitleFormats.GetName(path), outputFormat, StringComparison.OrdinalIgnoreCase))
        {
            // no sync, so converting is the whole job. Never over an existing file.
            var destination = UniqueConversionPath(path, outputFormat);

            try
            {
                await _converter.ConvertAsync(path, destination, cancellationToken: cancellationToken).ConfigureAwait(false);
                result.ConvertedFrom = SubtitleFormats.GetName(path);
                result.SubtitlePath = destination;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        if (request.Translation is not null)
        {
            result.Translation = await _translationService
                .TranslateAsync(result.SubtitlePath, request.Translation, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Gets the formats a subtitle can be converted to.
    /// </summary>
    [HttpGet("Lapse/Convert/Formats")]
    public ActionResult<IReadOnlyList<string>> GetConvertFormats()
    {
        return Ok(SubtitleFormats.OutputFormats);
    }

    // Never write over an existing subtitle, most likely the one converted last time.
    private static string UniqueConversionPath(string sourcePath, string format)
    {
        var destination = Path.ChangeExtension(sourcePath, "." + format);

        for (var attempt = 1; System.IO.File.Exists(destination); attempt++)
        {
            destination = Path.ChangeExtension(sourcePath, null)
                + "." + attempt.ToString(CultureInfo.InvariantCulture) + "." + format;
        }

        return destination;
    }

    // The conversion worked either way, so a file that won't delete just means RemovedOriginal stays false.
    private static bool TryDeleteFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
