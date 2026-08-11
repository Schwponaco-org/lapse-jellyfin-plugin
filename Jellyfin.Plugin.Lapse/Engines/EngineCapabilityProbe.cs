// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Asks an installed engine binary what it supports, rather than the plugin assuming a
/// version. Two ways, in order:
///
/// 1. "engine --capabilities", which prints JSON like
///    { "version": "1.3.0", "flags": ["--output", "--no-backup"] }.
/// 2. If that isn't a thing on this build, run it with no arguments and read the flags
///    out of the usage text it prints. Every engine here prints usage when it's called
///    wrong, and that text lists the optional flags, so it's a reliable second source
///    and it works on builds that predate --capabilities entirely.
///
/// If neither answers, the engine gets <see cref="EngineRuntimeInfo.Unknown"/> and the
/// plugin sticks to the flags that have always been there.
/// </summary>
public partial class EngineCapabilityProbe
{
    // Anything that looks like a long option in a usage line: --output, --no-backup, ...
    [GeneratedRegex(@"--[a-z][a-z0-9-]*", RegexOptions.IgnoreCase)]
    private static partial Regex LongFlagRegex();

    // A dotted version anywhere in what --version printed: "alass 2.0.0", "0.4.25", "v1.3.0"
    [GeneratedRegex(@"\bv?(?<version>[0-9]+\.[0-9]+(\.[0-9]+)?)\b")]
    private static partial Regex VersionRegex();

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] CapabilitiesArguments = { "--capabilities" };
    private static readonly string[] VersionArguments = { "--version" };

    private readonly ILogger<EngineCapabilityProbe> _logger;
    private readonly ConcurrentDictionary<string, EngineRuntimeInfo> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineCapabilityProbe"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public EngineCapabilityProbe(ILogger<EngineCapabilityProbe> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Throws away everything the probe remembers, so the next call re-asks the binaries.
    /// Called after an install or an update, since the file on disk just changed.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets what the binary at a path supports, asking it the first time and remembering
    /// the answer afterwards. The cache key includes the file's write time and size, so a
    /// binary replaced behind the plugin's back gets re-probed anyway.
    /// </summary>
    /// <param name="binaryPath">Full path to the engine binary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the binary supports.</returns>
    public async Task<EngineRuntimeInfo> ProbeAsync(string binaryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return EngineRuntimeInfo.Unknown;
        }

        var info = new FileInfo(binaryPath);
        var key = $"{binaryPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var probed = await RunProbeAsync(binaryPath, cancellationToken).ConfigureAwait(false);
        _cache[key] = probed;
        return probed;
    }

    private async Task<EngineRuntimeInfo> RunProbeAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var capabilities = await TryCapabilitiesCallAsync(binaryPath, cancellationToken).ConfigureAwait(false);
        if (capabilities is not null)
        {
            _logger.LogInformation(
                "{Path} answered --capabilities: version {Version}, flags {Flags}",
                binaryPath,
                capabilities.Version ?? "unknown",
                string.Join(", ", capabilities.Flags));
            return capabilities;
        }

        var fromUsage = await TryUsageTextAsync(binaryPath, cancellationToken).ConfigureAwait(false);
        if (fromUsage is not null)
        {
            // Most engines that don't answer --capabilities still answer --version, and
            // that's the only way to put a version on a binary the plugin didn't install
            // itself. Nothing breaks when it isn't supported: the engine prints its usage
            // text, no version comes out of it, and the card just doesn't show one.
            fromUsage.Version = await TryVersionCallAsync(binaryPath, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "{Path} has no --capabilities, read {Flags} out of its usage text instead (version {Version})",
                binaryPath,
                string.Join(", ", fromUsage.Flags),
                fromUsage.Version ?? "not reported");
            return fromUsage;
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

        // An engine that doesn't know --version prints its usage text, which is full of
        // things that aren't versions, so only trust output that is short enough to be a
        // real answer.
        var text = (stdout + "\n" + stderr).Trim();
        if (text.Length == 0 || text.Length > 120)
        {
            return null;
        }

        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups["version"].Value : null;
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
            // an engine that doesn't know --capabilities may well print usage text and
            // exit 0, which isn't JSON. Nothing wrong, just not an answer.
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The path comes from the plugin's own engines folder or an admin-set override, the same value the runner already executes.")]
    private static async Task<(string Stdout, string Stderr, int ExitCode, bool Started)> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (string.Empty, string.Empty, -1, false);
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
            // a binary that sits there rather than printing usage tells us nothing useful,
            // and we're not going to wait around for it
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // exited on its own in the meantime
            }

            return (string.Empty, string.Empty, -1, false);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (stdout, stderr, process.ExitCode, true);
    }
}
