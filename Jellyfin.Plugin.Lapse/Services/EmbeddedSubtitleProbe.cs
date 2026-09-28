// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Asks ffprobe which subtitle streams a video file holds right now.
///
/// Jellyfin's database already knows, most of the time, and that is what everything else
/// in the plugin reads. It can be wrong in ways that matter the moment a track is being
/// pulled out by number: the file was replaced by an upgrade and not rescanned yet, or the
/// library is set to not keep embedded subtitles at all, in which case the database has
/// none of them. ffprobe reads the same container ffmpeg is about to, so the index it
/// gives back is the index -map 0:N will land on.
/// </summary>
public class EmbeddedSubtitleProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<EmbeddedSubtitleProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSubtitleProbe"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the ffprobe Jellyfin ships.</param>
    /// <param name="logger">Logger.</param>
    public EmbeddedSubtitleProbe(IMediaEncoder mediaEncoder, ILogger<EmbeddedSubtitleProbe> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether there is an ffprobe to ask.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            var path = _mediaEncoder.ProbePath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    /// <summary>
    /// Lists the subtitle streams in a video file.
    /// </summary>
    /// <param name="videoPath">The video file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The streams, in container order, or null when ffprobe couldn't be run or
    /// couldn't read the file. An empty list means it read the file and there are none.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The video path is Jellyfin's own resolved path for an item the caller already looked up.")]
    public async Task<List<EmbeddedSubtitleTrack>?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.ProbePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ProcessOutput.ReadAsUtf8(startInfo);

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("s");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=index,codec_name:stream_tags=language,title:stream_disposition=forced,hearing_impaired");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");

        // file: stops a name that happens to look like a protocol ("concat:...", or
        // anything with a colon early on) from being read as one.
        startInfo.ArgumentList.Add(FfmpegPaths.AsFileUrl(videoPath));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not start ffprobe to look at {Video}", videoPath);
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FfmpegPaths.TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogWarning("ffprobe took too long reading {Video}", videoPath);
            return null;
        }

        string stdout;
        string stderr;

        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffprobe could not read {Video}: {Error}", videoPath, stderr.Trim());
            return null;
        }

        var tracks = Parse(stdout);
        if (tracks is null)
        {
            _logger.LogWarning("ffprobe's answer for {Video} didn't parse", videoPath);
        }

        return tracks;
    }

    /// <summary>
    /// Reads ffprobe's JSON answer. Public so it can be checked against real ffprobe
    /// output without starting a process.
    /// </summary>
    /// <param name="json">What ffprobe printed.</param>
    /// <returns>The subtitle streams, or null when it wasn't the JSON expected.</returns>
    public static List<EmbeddedSubtitleTrack>? Parse(string json)
    {
        var result = new List<EmbeddedSubtitleTrack>();

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

            if (!document.RootElement.TryGetProperty("streams", out var streams))
            {
                // A file with no subtitle streams at all comes back as "{}" or with an
                // empty list, depending on the ffprobe build. Both mean none.
                return result;
            }

            if (streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                // TryGetInt32 throws rather than failing on anything that isn't a number, so
                // the kind is checked first.
                if (stream.ValueKind != JsonValueKind.Object
                    || !stream.TryGetProperty("index", out var indexElement)
                    || indexElement.ValueKind != JsonValueKind.Number
                    || !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }

                var track = new EmbeddedSubtitleTrack
                {
                    Index = index,
                    Codec = GetString(stream, "codec_name")
                };

                if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    // Matroska tag names come back in whatever case the muxer wrote them.
                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase))
                        {
                            track.Language = Blank(tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null);
                        }
                        else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase))
                        {
                            track.Title = Blank(tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null);
                        }
                    }
                }

                if (stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object)
                {
                    track.ForcedFlag = IsSet(disposition, "forced");
                    track.HearingImpairedFlag = IsSet(disposition, "hearing_impaired");
                }

                result.Add(track);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Blank(value.GetString())
            : null;
    }

    private static bool IsSet(JsonElement disposition, string name)
    {
        return disposition.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var set)
            && set != 0;
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
