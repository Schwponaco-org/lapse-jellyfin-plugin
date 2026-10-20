// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Asks an engine binary what it supports instead of guessing from a version:
/// 1. "engine --capabilities" (JSON with version and flags)
/// 2. otherwise run it with no args and read the flags out of its usage text
/// If neither works it's <see cref="EngineRuntimeInfo.Unknown"/> and we stick to the safe flags.
///
/// Answers are cached per binary (path + size + write time). The cache holds the running
/// task, so the dashboard and a sync asking at the same moment share one probe.
/// </summary>
public partial class EngineCapabilityProbe
{
    // Anything that looks like a long option in a usage line: --output, --no-backup, ...
    [GeneratedRegex(@"--[a-z][a-z0-9-]*", RegexOptions.IgnoreCase)]
    private static partial Regex LongFlagRegex();

    // A dotted version anywhere in what --version printed: "alass 2.0.0", "0.4.25", "v1.3.0"
    [GeneratedRegex(@"\bv?(?<version>[0-9]+\.[0-9]+(\.[0-9]+)?)\b")]
    private static partial Regex VersionRegex();

    // One subtitle extension on a line of its own, which is what --formats prints.
    [GeneratedRegex(@"^\s*\*?\.?(?<extension>[a-z0-9]{2,5})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatLineRegex();

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RunnableTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] CapabilitiesArguments = { "--capabilities" };
    private static readonly string[] VersionArguments = { "--version" };
    private static readonly string[] FormatsArguments = { "--formats" };
    private static readonly string[] VadArguments = { "--vad" };

    private readonly ILogger<EngineCapabilityProbe> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<EngineRuntimeInfo>>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _vadCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _runnableCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineCapabilityProbe"/> class.
    /// </summary>
    public EngineCapabilityProbe(ILogger<EngineCapabilityProbe> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Forgets everything, called after an install or update.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
        _vadCache.Clear();
        _runnableCache.Clear();
    }

    /// <summary>
    /// Gets what a binary supports, probing it the first time.
    /// </summary>
    public Task<EngineRuntimeInfo> ProbeAsync(string binaryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return Task.FromResult(EngineRuntimeInfo.Unknown);
        }

        // shared between callers, so it runs on its own token (every process has a timeout anyway)
        var probe = _cache.GetOrAdd(CacheKey(binaryPath), _ => new Lazy<Task<EngineRuntimeInfo>>(() => RunProbeAsync(binaryPath, CancellationToken.None)));
        return probe.Value.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Checks a binary actually starts (a download can still miss a shared library). Only
    /// a good answer is cached, a problem can get fixed without the file changing.
    /// </summary>
    public async Task<string?> CheckRunnableAsync(string binaryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return "not installed yet";
        }

        var key = CacheKey(binaryPath);
        if (_runnableCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? problem;

        try
        {
            // no args fails fast either way (usage error or loader error)
            var result = await Services.ProcessRunner.RunAsync(binaryPath, Array.Empty<string>(), RunnableTimeout, cancellationToken)
                .ConfigureAwait(false);

            problem = result.Stderr.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase)
                ? result.Stderr.Trim()
                : null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            problem = ex.Message;
        }
        catch (TimeoutException)
        {
            // still running means it loaded, good enough
            problem = null;
        }

        if (problem is null)
        {
            _runnableCache[key] = null;
        }

        return problem;
    }

    /// <summary>
    /// Asks LAPSE which voice detector it's running (lapse --vad): "silero" when the ONNX
    /// model loaded, "libfvad" when it fell back to the weaker one because onnxruntime or
    /// the model wasn't next to the binary. That fallback syncs worse with no error anywhere
    /// else, so the dashboard warns about it. LAPSE only.
    /// </summary>
    public Task<string?> ProbeVadBackendAsync(string binaryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return Task.FromResult<string?>(null);
        }

        var probe = _vadCache.GetOrAdd(CacheKey(binaryPath), _ => new Lazy<Task<string?>>(async () =>
        {
            var (stdout, _, _, started) = await RunAsync(binaryPath, VadArguments, CancellationToken.None).ConfigureAwait(false);
            return started ? ParseVadBackend(stdout) : null;
        }));

        return probe.Value.WaitAsync(cancellationToken);
    }

    // size and write time in the key, so a binary swapped behind our back gets probed again
    private static string CacheKey(string binaryPath)
    {
        var info = new FileInfo(binaryPath);
        return $"{binaryPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
    }

    private static string? ParseVadBackend(string stdout)
    {
        var text = stdout.Trim();
        if (string.Equals(text, "silero", StringComparison.OrdinalIgnoreCase))
        {
            return "silero";
        }

        if (string.Equals(text, "libfvad", StringComparison.OrdinalIgnoreCase))
        {
            return "libfvad";
        }

        return null;
    }

    private async Task<EngineRuntimeInfo> RunProbeAsync(string binaryPath, CancellationToken cancellationToken)
    {
        // Formats decide whether we convert before handing a file over, so always ask.
        // Independent calls, run them side by side (some engines take a while to start).
        var formatsCall = TryFormatsCallAsync(binaryPath, cancellationToken);
        var capabilitiesCall = TryCapabilitiesCallAsync(binaryPath, cancellationToken);
        var formats = await formatsCall.ConfigureAwait(false);
        var capabilities = await capabilitiesCall.ConfigureAwait(false);

        if (capabilities is not null)
        {
            capabilities.ReportedExtensions.AddRange(formats);

            _logger.LogInformation(
                "{Path} answered --capabilities: version {Version}, flags {Flags}",
                binaryPath,
                capabilities.Version ?? "unknown",
                string.Join(", ", capabilities.Flags));
            return capabilities;
        }

        // no --capabilities: read the usage text, and ask --version too since that's the
        // only way to put a version on a binary we didn't install
        var usageCall = TryUsageTextAsync(binaryPath, cancellationToken);
        var versionCall = TryVersionCallAsync(binaryPath, cancellationToken);
        var fromUsage = await usageCall.ConfigureAwait(false);
        var version = await versionCall.ConfigureAwait(false);

        if (fromUsage is not null)
        {
            fromUsage.Version = version;
            fromUsage.ReportedExtensions.AddRange(formats);

            _logger.LogDebug(
                "{Path} has no --capabilities, read {Flags} out of its usage text instead (version {Version})",
                binaryPath,
                string.Join(", ", fromUsage.Flags),
                version ?? "not reported");
            return fromUsage;
        }

        if (version is not null || formats.Count > 0)
        {
            _logger.LogDebug("{Path} only answered --version ({Version}), sticking to the safe flags", binaryPath, version);

            var partial = new EngineRuntimeInfo
            {
                Probed = true,
                Source = "version",
                Version = version
            };

            partial.ReportedExtensions.AddRange(formats);
            return partial;
        }

        _logger.LogDebug("Could not work out what {Path} supports, falling back to the safe defaults", binaryPath);
        return EngineRuntimeInfo.Unknown;
    }

    private async Task<string?> TryVersionCallAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var (stdout, stderr, _, started) = await RunAsync(binaryPath, VersionArguments, cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            return null;
        }

        // An engine without --version prints its usage text, full of numbers that aren't
        // versions. A real answer is a line or two.
        var text = (stdout + "\n" + stderr).Trim();
        if (text.Length == 0 || text.Length > 200 || CountLines(text) > 3)
        {
            return null;
        }

        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups["version"].Value : null;
    }

    // "--formats" prints one extension per line. Engines without it error or print usage,
    // so the strict per-line parse tells them apart. Anything off and we use the built-in list.
    private async Task<List<string>> TryFormatsCallAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var found = new List<string>();

        var (stdout, _, exitCode, started) = await RunAsync(binaryPath, FormatsArguments, cancellationToken).ConfigureAwait(false);
        if (!started || exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return found;
        }

        foreach (var line in stdout.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = FormatLineRegex().Match(line);
            if (!match.Success)
            {
                // one line that isn't an extension means this wasn't a format list at all
                _logger.LogDebug("{Path} answered --formats with something that isn't a format list, ignoring it", binaryPath);
                return new List<string>();
            }

            var extension = "." + match.Groups["extension"].Value.ToLowerInvariant();
            if (!found.Contains(extension, StringComparer.Ordinal))
            {
                found.Add(extension);
            }
        }

        if (found.Count > 0)
        {
            _logger.LogInformation("{Path} reads {Formats}", binaryPath, string.Join(", ", found));
        }

        return found;
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private async Task<EngineRuntimeInfo?> TryCapabilitiesCallAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var (stdout, _, exitCode, started) = await RunAsync(binaryPath, CapabilitiesArguments, cancellationToken).ConfigureAwait(false);
        if (!started || exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new EngineRuntimeInfo { Probed = true, Source = "capabilities" };

            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            {
                result.Version = version.GetString();
            }

            if (root.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
            {
                foreach (var flag in flags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.String && flag.GetString() is { } value)
                    {
                        result.Flags.Add(value);
                    }
                }
            }

            return result;
        }
        catch (JsonException)
        {
            // usage text with exit 0, not an answer
            return null;
        }
    }

    private async Task<EngineRuntimeInfo?> TryUsageTextAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var (stdout, stderr, _, started) = await RunAsync(binaryPath, Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            return null;
        }

        var text = stdout + "\n" + stderr;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var result = new EngineRuntimeInfo
        {
            Probed = true,
            Source = "usage",
            UsageText = text.Length > 4000 ? text[..4000] : text
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LongFlagRegex().Matches(text))
        {
            if (seen.Add(match.Value))
            {
                result.Flags.Add(match.Value);
            }
        }

        return result.Flags.Count > 0 ? result : null;
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode, bool Started)> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Services.ProcessRunner.RunAsync(binaryPath, arguments, ProbeTimeout, cancellationToken).ConfigureAwait(false);
            return (result.Stdout, result.Stderr, result.ExitCode, true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            // didn't start or didn't answer, either way nothing useful
            return (string.Empty, string.Empty, -1, false);
        }
    }
}
