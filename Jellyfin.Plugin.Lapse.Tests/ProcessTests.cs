// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.ComponentModel;
using System.Diagnostics;
using Jellyfin.Plugin.Lapse.Engines;
using Jellyfin.Plugin.Lapse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// The one place child processes are started from, and the probe that asks an engine
/// binary what it can do. Both run real processes: /bin/sh, and a shell script standing
/// in for an engine.
/// </summary>
public class ProcessTests
{
    // Answers the calls the probe makes the way LAPSE does, and counts how often it was
    // started so the caching can be checked.
    private const string FakeEngine = """
        #!/bin/sh
        echo run >> "$0.calls"
        case "$1" in
          --formats) printf '.srt\n.ass\n.sup\n' ;;
          --capabilities) echo '{"version":"9.9.9","flags":["--output","--json","--batch","--fps"]}' ;;
          --vad) echo silero ;;
          *) echo "Usage: lapse <video> [subtitle] [auto|ols|nosplit|split]" >&2; exit 1 ;;
        esac
        """;

    private static string WriteScript(string body, string name = "engine")
    {
        var path = Path.Combine(TestHost.NewFolder(), name);
        File.WriteAllText(path, body.Replace("\r\n", "\n", StringComparison.Ordinal));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static int Calls(string engine)
    {
        var log = engine + ".calls";
        return File.Exists(log) ? File.ReadAllLines(log).Length : 0;
    }

    [Fact]
    public async Task ReadsBothPipesAndTheExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "echo out; echo err >&2; exit 3" }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Stdout.Trim());
        Assert.Equal("err", result.Stderr.Trim());
    }

    [Fact]
    public async Task PassesArgumentsWithoutAShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A shell would split this on the space and expand the dollar sign.
        var result = await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "printf '%s' \"$1\"", "sh", "a b $HOME" }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("a b $HOME", result.Stdout);
    }

    [Fact]
    public async Task ATimeoutKillsItAndSaysSo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 30" }, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("sh took longer than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingIsACancellationNotATimeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // This used to come back as "took too long", which is not what happened when
        // somebody pressed Stop.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 30" }, TimeSpan.FromMinutes(5), cancel.Token));
    }

    [Fact]
    public async Task AProgramThatIsNotThereFailsToStart()
    {
        await Assert.ThrowsAsync<Win32Exception>(() =>
            ProcessRunner.RunAsync(Path.Combine(TestHost.NewFolder(), "nothing-here"), Array.Empty<string>(), null, CancellationToken.None));
    }

    [Fact]
    public async Task TheProbeReadsCapabilitiesAndFormatsAndAsksOnlyOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteScript(FakeEngine, "lapse");
        var probe = new EngineCapabilityProbe(NullLogger<EngineCapabilityProbe>.Instance);

        var first = await probe.ProbeAsync(engine);
        var callsAfterFirst = Calls(engine);
        var second = await probe.ProbeAsync(engine);

        Assert.True(first.Probed);
        Assert.Equal("9.9.9", first.Version);
        Assert.True(first.SupportsOutputFlag);
        Assert.True(first.SupportsBatch);
        Assert.Equal(new[] { ".srt", ".ass", ".sup" }, first.ReportedExtensions);
        Assert.True(first.CanRead("lapse", "/m/Film.sup"));
        Assert.False(first.CanRead("lapse", "/m/Film.vtt"));
        Assert.Same(first, second);
        Assert.Equal(callsAfterFirst, Calls(engine));

        Assert.Equal("silero", await probe.ProbeVadBackendAsync(engine));
    }

    [Fact]
    public async Task ABinaryThatStartsIsCheckedOnceAndOneThatDoesntIsCheckedAgain()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteScript(FakeEngine, "lapse");
        var probe = new EngineCapabilityProbe(NullLogger<EngineCapabilityProbe>.Instance);

        Assert.Null(await probe.CheckRunnableAsync(engine));
        var calls = Calls(engine);
        Assert.Null(await probe.CheckRunnableAsync(engine));
        Assert.Equal(calls, Calls(engine));

        const string Broken = "#!/bin/sh\necho 'error while loading shared libraries' >&2\n";
        const string Fixed = "#!/bin/sh\necho 'usage: lapse <video> <subtitle> mode' >&2\n";
        Assert.Equal(Broken.Length, Fixed.Length);

        var broken = WriteScript(Broken, "broken");
        var stamp = File.GetLastWriteTimeUtc(broken);
        Assert.Contains("error while loading shared libraries", await probe.CheckRunnableAsync(broken), StringComparison.Ordinal);

        // Fixed without the file's size or time changing, the way installing a missing
        // library fixes it, so a problem must not be remembered.
        File.WriteAllText(broken, Fixed);
        File.SetLastWriteTimeUtc(broken, stamp);
        Assert.Null(await probe.CheckRunnableAsync(broken));

        Assert.Equal("not installed yet", await probe.CheckRunnableAsync(engine + ".missing"));
    }

    [Fact]
    public async Task AnEngineWithoutCapabilitiesIsReadFromItsUsageText()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteScript("""
            #!/bin/sh
            case "$1" in
              --version) echo "alass 2.0.0" ;;
              *) echo "USAGE: alass <reference> <incorrect> <output> [--split-penalty N] [--no-split] [--interval MS]" >&2; exit 2 ;;
            esac
            """);

        var runtime = await new EngineCapabilityProbe(NullLogger<EngineCapabilityProbe>.Instance).ProbeAsync(engine);

        Assert.Equal("usage", runtime.Source);
        Assert.Equal("2.0.0", runtime.Version);
        Assert.True(runtime.HasFlag("--no-split"));
        Assert.False(runtime.SupportsOutputFlag);
        Assert.Empty(runtime.ReportedExtensions);
    }
}
