// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lapse.Data;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services;

/// <summary>
/// Asks ffprobe which subtitle streams a video has right now. Jellyfin's DB is usually
/// right, but not after an unscanned upgrade or when the library doesn't keep embedded
/// subtitle info. ffprobe's index is exactly what -map 0:N will hit.
/// </summary>
public class EmbeddedSubtitleProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private const int MaxCached = 256;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<EmbeddedSubtitleProbe> _logger;
    private readonly ConcurrentDictionary<string, ((long Length, DateTime WriteTimeUtc) Stamp, DateTime ReadUtc, List<EmbeddedSubtitleTrack> Tracks)> _recent = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedSubtitleProbe"/> class.
    /// </summary>
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
    public async Task<List<EmbeddedSubtitleTrack>?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        // extracting every track asks about the same file once per track, cache it short term
        var stamp = Stamp(videoPath);
        if (stamp is not null
            && _recent.TryGetValue(videoPath, out var cached)
            && cached.Stamp == stamp
            && DateTime.UtcNow - cached.ReadUtc < CacheFor)
        {
            return Copy(cached.Tracks);
        }

        var arguments = new[]
        {
            "-v",
            "error",
            "-select_streams",
            "s",
            "-show_entries",
            "stream=index,codec_name:stream_tags=language,title:stream_disposition=forced,hearing_impaired",
            "-of",
            "json",

            // file: so a name like "concat:..." isn't read as a protocol
            FfmpegPaths.AsFileUrl(videoPath)
        };

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(_mediaEncoder.ProbePath, arguments, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not start ffprobe to look at {Video}", videoPath);
            return null;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("ffprobe took too long reading {Video}", videoPath);
            return null;
        }

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("ffprobe could not read {Video}: {Error}", videoPath, result.Stderr.Trim());
            return null;
        }

        var tracks = Parse(result.Stdout);
        if (tracks is null)
        {
            _logger.LogWarning("ffprobe's answer for {Video} didn't parse", videoPath);
            return null;
        }

        if (stamp is not null)
        {
            if (_recent.Count >= MaxCached)
            {
                _recent.Clear();
            }

            _recent[videoPath] = (stamp.Value, DateTime.UtcNow, tracks);
        }

        return Copy(tracks);
    }

    // callers fill in blanks, so hand out copies, not the cached objects
    private static List<EmbeddedSubtitleTrack> Copy(List<EmbeddedSubtitleTrack> tracks)
    {
        return tracks.ConvertAll(t => new EmbeddedSubtitleTrack
        {
            Index = t.Index,
            Codec = t.Codec,
            Language = t.Language,
            Title = t.Title,
            ForcedFlag = t.ForcedFlag,
            HearingImpairedFlag = t.HearingImpairedFlag
        });
    }

    private static (long Length, DateTime WriteTimeUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses ffprobe's JSON. Public for the tests.
    /// </summary>
    public static List<EmbeddedSubtitleTrack>? Parse(string json)
    {
        var result = new List<EmbeddedSubtitleTrack>();

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

            if (!document.RootElement.TryGetProperty("streams", out var streams))
            {
                // no subtitle streams comes back as "{}" or an empty list depending on the build
                return result;
            }

            if (streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                // TryGetInt32 throws on non-numbers, check the kind first
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
                    // tag name case depends on the muxer
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
