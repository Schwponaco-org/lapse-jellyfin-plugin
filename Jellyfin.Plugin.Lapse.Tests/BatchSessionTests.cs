// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Diagnostics;
using Jellyfin.Plugin.Lapse.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// Drives the batch session against a stand-in engine: a shell script that speaks the
/// same protocol as lapse --batch. Answers on stdout, reasons on stderr, and the odd job
/// that fails, dies or hangs.
/// </summary>
public class BatchSessionTests
{
    private const string FakeEngine = """
        #!/bin/sh
        if [ "$1" != "--batch" ]; then echo "usage" >&2; exit 1; fi
        while IFS= read -r line; do
          case "$line" in
            *fail*) echo "decoding 100%" >&2; echo "Only 3 cues in x.srt, that is not enough to go on. Pass --force to sync it anyway." >&2; echo '{"error":true}' ;;
            *die*) echo "segfault, more or less" >&2; exit 3 ;;
            *hang*) sleep 30 ;;
            *) echo "decoding 50%" >&2; echo "{\"verdict\":\"solid\",\"written\":true,\"pid\":$$}" ;;
          esac
        done
        """;

    private static string WriteFakeEngine()
    {
        var path = Path.Combine(TestHost.NewFolder(), "lapse");
        File.WriteAllText(path, FakeEngine.Replace("\r\n", "\n", StringComparison.Ordinal));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static int Pid(string reply)
    {
        var at = reply.IndexOf("\"pid\":", StringComparison.Ordinal) + 6;
        return int.Parse(reply[at..reply.IndexOf('}', at)], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task KeepsOneProcessAcrossJobs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteFakeEngine();
        using var session = new EngineBatchSession(NullLogger.Instance);

        var first = await session.TryRunAsync(engine, new[] { "/m/a.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);
        var second = await session.TryRunAsync(engine, new[] { "/m/b b.mkv", "/m/b.srt" }, _ => { }, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(0, first.Value.ExitCode);
        Assert.Equal(Pid(first.Value.Stdout), Pid(second.Value.Stdout));
        Assert.Equal(2, session.JobsAnswered);
    }

    [Fact]
    public async Task AFailedJobComesBackWithItsReason()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteFakeEngine();
        using var session = new EngineBatchSession(NullLogger.Instance);

        var answer = await session.TryRunAsync(engine, new[] { "/m/fail.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(1, answer.Value.ExitCode);
        Assert.Contains("Pass --force", answer.Value.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, answer.Value.Stdout);
    }

    [Fact]
    public async Task ADeadProcessHandsTheJobBackAndTheNextOneStartsAgain()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteFakeEngine();
        using var session = new EngineBatchSession(NullLogger.Instance);

        var died = await session.TryRunAsync(engine, new[] { "/m/die.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);
        var next = await session.TryRunAsync(engine, new[] { "/m/a.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);

        Assert.Null(died);
        Assert.NotNull(next);
        Assert.Equal(0, next.Value.ExitCode);
    }

    [Fact]
    public async Task CancellingStopsTheProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = WriteFakeEngine();
        using var session = new EngineBatchSession(NullLogger.Instance);

        var warm = await session.TryRunAsync(engine, new[] { "/m/a.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);
        var pid = Pid(warm!.Value.Stdout);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.TryRunAsync(engine, new[] { "/m/hang.mkv", "/m/a.srt" }, _ => { }, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));

        var after = await session.TryRunAsync(engine, new[] { "/m/a.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);
        Assert.NotEqual(pid, Pid(after!.Value.Stdout));
    }

    [Fact]
    public async Task APathItCantCarryIsLeftToAOneShotRun()
    {
        var engine = OperatingSystem.IsWindows() ? "lapse.exe" : WriteFakeEngine();
        using var session = new EngineBatchSession(NullLogger.Instance);

        var answer = await session.TryRunAsync(engine, new[] { "/m/The \"Best\" Film.mkv", "/m/a.srt" }, _ => { }, CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal(0, session.JobsAnswered);
    }
}
