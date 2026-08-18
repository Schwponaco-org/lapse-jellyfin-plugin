// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Pulls a subtitle track out of the video file it's baked into and writes it next to the
/// video as an ordinary sidecar.
///
/// Most films only ever have embedded subtitles - they came in the mkv and nobody ever
/// put an .srt beside them - and an engine can't read a track that only exists inside a
/// container. Extracting one turns it into a normal subtitle file that syncing, shifting,
/// converting and translating all work on, and that Jellyfin picks up as another track on
/// its next scan, so nothing is lost by doing it.
///
/// Only the text based tracks can come out this way. PGS and VobSub are sequences of
/// pictures with no characters in them at all.
/// </summary>
public class SubtitleExtractor
{
    /// <summary>
    /// The prefix that marks a subtitle option as one still inside the video file. The
    /// number after it is the stream index ffmpeg and Jellyfin both use.
    /// </summary>
    public const string EmbeddedPrefix = "embedded://";

    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(5);

    // What each subtitle codec should land on disk as. Anything not in here either isn't
    // text or isn't something the plugin can work with afterwards.
    private static readonly Dictionary<string, string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subrip"] = ".srt",
        ["srt"] = ".srt",
        ["text"] = ".srt",
        ["mov_text"] = ".srt",
        ["ass"] = ".ass",
        ["ssa"] = ".ssa",
        ["webvtt"] = ".vtt",
        ["vtt"] = ".vtt"
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SubtitleExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleExtractor"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the ffmpeg Jellyfin ships.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleExtractor(IMediaEncoder mediaEncoder, ILogger<SubtitleExtractor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a subtitle option refers to a track inside the video rather than to a
    /// file on disk.
    /// </summary>
    /// <param name="path">The option's path.</param>
    /// <returns>True for an embedded track.</returns>
    public static bool IsEmbedded(string? path)
    {
        return path is not null && path.StartsWith(EmbeddedPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the identifier used in place of a path for an embedded track.
    /// </summary>
    /// <param name="streamIndex">The stream's index in the container.</param>
    /// <returns>The identifier.</returns>
    public static string BuildKey(int streamIndex)
    {
        return EmbeddedPrefix + streamIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the stream index back out of an embedded track's identifier.
    /// </summary>
    /// <param name="path">The identifier.</param>
    /// <param name="streamIndex">The index.</param>
    /// <returns>True if it was one of ours and held a number.</returns>
    public static bool TryParseKey(string? path, out int streamIndex)
    {
        streamIndex = -1;

        return IsEmbedded(path)
            && int.TryParse(path![EmbeddedPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out streamIndex);
    }

    /// <summary>
    /// Gets the file extension a subtitle codec should be written as, or null when that
    /// codec holds pictures rather than text.
    /// </summary>
    /// <param name="codec">The codec name Jellyfin reported.</param>
    /// <returns>An extension including the dot, or null.</returns>
    public static string? GetExtensionForCodec(string? codec)
    {
        return codec is not null && TextCodecs.TryGetValue(codec, out var extension) ? extension : null;
    }

    /// <summary>
    /// Says why a track can't be extracted, or null when it can be.
    /// </summary>
    /// <param name="codec">The codec name Jellyfin reported.</param>
    /// <returns>A message for the caller, or null.</returns>
    public string? GetExtractionProblem(string? codec)
    {
        if (GetExtensionForCodec(codec) is null)
        {
            return $"{(codec ?? "That").ToUpperInvariant()} subtitles are pictures of text rather than text, so there's nothing to extract or line up. They need OCR first, with something like Subtitle Edit.";
        }

        return HasFfmpeg()
            ? null
            : "Getting a subtitle out of a video file needs ffmpeg, and the server hasn't told the plugin where its copy is.";
    }

    /// <summary>
    /// Copies one subtitle track out of a video into a file beside it.
    /// </summary>
    /// <param name="videoPath">The video holding the track.</param>
    /// <param name="streamIndex">The track's stream index in the container.</param>
    /// <param name="codec">The track's codec, which decides the file extension.</param>
    /// <param name="language">The track's language, used in the file name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file that was written.</returns>
    /// <exception cref="NotSupportedException">The track isn't text, or ffmpeg is missing.</exception>
    /// <exception cref="InvalidDataException">ffmpeg couldn't produce a subtitle.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path for an item the caller already looked up, and the destination is derived from it.")]
    public async Task<string> ExtractAsync(
        string videoPath,
        int streamIndex,
        string? codec,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (GetExtractionProblem(codec) is { } problem)
        {
            throw new NotSupportedException(problem);
        }

        var extension = GetExtensionForCodec(codec)!;
        var destination = BuildDestination(videoPath, streamIndex, language, extension);

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Copy the track through rather than re-encoding it: the text is already in the
        // shape we want, and a copy keeps whatever styling an ass track carries.
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:" + streamIndex.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c:s");
        startInfo.ArgumentList.Add(extension == ".vtt" ? "webvtt" : "copy");
        startInfo.ArgumentList.Add(destination);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NotSupportedException("Could not start ffmpeg to extract that subtitle: " + ex.Message, ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(destination);
            throw new TimeoutException("ffmpeg took too long getting that subtitle out of the video.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            _logger.LogWarning(
                "ffmpeg could not extract subtitle stream {Index} from {Video}: {Error}",
                streamIndex,
                videoPath,
                stderr);

            TryDelete(destination);
            throw new InvalidDataException("ffmpeg couldn't get that subtitle track out of the video file.");
        }

        _logger.LogInformation(
            "Extracted subtitle stream {Index} from {Video} to {Destination}",
            streamIndex,
            videoPath,
            destination);

        return destination;
    }

    /// <summary>
    /// Turns a picked subtitle option into a real file on disk. An external one already
    /// is one; a track still inside the video is pulled out next to it first. From then on
    /// it is an ordinary sidecar, so this only ever does the extraction once per track.
    /// </summary>
    /// <param name="item">The item the subtitle belongs to.</param>
    /// <param name="option">The subtitle that was picked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file path, or a reason it couldn't be produced.</returns>
    public async Task<(string? Path, string? Error)> ResolveAsync(
        BaseItem item,
        SubtitleOption option,
        CancellationToken cancellationToken = default)
    {
        if (!option.IsEmbedded)
        {
            return (option.Path, null);
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return (null, "That item has no video file to extract from.");
        }

        if (!TryParseKey(option.Path, out var streamIndex))
        {
            return (null, "That subtitle track couldn't be identified.");
        }

        try
        {
            var extracted = await ExtractAsync(item.Path, streamIndex, option.Codec, option.Language, cancellationToken)
                .ConfigureAwait(false);

            return (extracted, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or TimeoutException)
        {
            return (null, ex.Message);
        }
    }

    private bool HasFfmpeg()
    {
        var path = _mediaEncoder.EncoderPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    // Named the way a subtitle beside a video is normally named, so Jellyfin reads the
    // language off it on the next scan and it sits with the rest of them. The stream
    // index goes in as well, since one video can carry several tracks in one language.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Built from Jellyfin's own resolved video path, the language tag off the media stream, an integer stream index and an extension from a fixed table. Nothing from the request reaches it.")]
    private static string BuildDestination(string videoPath, int streamIndex, string? language, string extension)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var tag = string.IsNullOrWhiteSpace(language) ? "und" : language.Trim().ToLowerInvariant();

        var destination = Path.Combine(folder, $"{stem}.{tag}{extension}");

        // Never write over a subtitle that's already sitting there, whoever put it there.
        var attempt = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(
                folder,
                $"{stem}.{tag}.track{streamIndex.ToString(CultureInfo.InvariantCulture)}{(attempt > 1 ? "." + attempt.ToString(CultureInfo.InvariantCulture) : string.Empty)}{extension}");
            attempt++;
        }

        return destination;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // finished on its own in the meantime
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Only ever removes the half written output file this class just named.")]
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // a leftover empty file is untidy, not a failure worth reporting over the
            // one that actually went wrong
        }
    }
}
