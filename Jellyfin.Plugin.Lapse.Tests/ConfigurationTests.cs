// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Configuration;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// The configuration and what reads it: lookups that mustn't change it, the ignore list,
/// the schedules, and the deferred save the sync queue writes through.
/// </summary>
public class ConfigurationTests
{
    public ConfigurationTests()
    {
        _ = TestHost.Plugin;
    }

    [Fact]
    public void LookingUpSettingsDoesNotAddThem()
    {
        var config = new PluginConfiguration();

        Assert.Null(config.FindEngineSettings("alass"));
        Assert.Empty(config.Engines);

        var created = config.GetEngineSettings("alass");
        Assert.Same(created, config.FindEngineSettings("ALASS"));
        Assert.Single(config.Engines);

        var library = Guid.NewGuid();
        Assert.Null(config.FindLibraryConfig(library));
        Assert.True(config.IsLibraryEnabled(library));
        Assert.Empty(config.Libraries);

        config.GetLibraryConfig(library).Enabled = false;
        Assert.False(config.IsLibraryEnabled(library));
    }

    [Theory]
    [InlineData(".shifted", ".shifted")]
    [InlineData("synced", ".synced")]
    [InlineData("  .lapse ", ".lapse")]
    [InlineData("", ".shifted")]
    [InlineData(null, ".shifted")]
    public void TheSidecarSuffixAlwaysHasItsDot(string? saved, string expected)
    {
        var config = new PluginConfiguration { SidecarSuffix = saved! };
        Assert.Equal(expected, config.ResolveSidecarSuffix());
    }

    [Fact]
    public void AnIgnoredFolderCoversWhatIsUnderItAndNothingBeside()
    {
        var config = new PluginConfiguration();
        var series = Guid.NewGuid();
        config.IgnoreRules.Add(new IgnoreRule { Path = "/media/Movies/" });
        config.IgnoreRules.Add(new IgnoreRule { ItemId = series });

        Assert.True(config.IsIgnored(Array.Empty<Guid>(), "/media/Movies/Film/Film.mkv"));
        Assert.True(config.IsIgnored(Array.Empty<Guid>(), "/media/movies"));
        Assert.False(config.IsIgnored(Array.Empty<Guid>(), "/media/Movies Extra/Film.mkv"));
        Assert.True(config.IsIgnored(new[] { Guid.NewGuid(), series }, "/tv/Show/S01E01.mkv"));
        Assert.False(config.IsIgnored(new[] { Guid.NewGuid() }, null));
    }

    [Fact]
    public void LegacySettingsMoveOntoTheLapseEngineOnce()
    {
        var config = new PluginConfiguration
        {
            EngineBinaryPathOverride = "/opt/lapse",
            DefaultPenalty = 9,
            ConfidenceSigma = 0,
            SidecarSuffix = " "
        };

        config.MigrateLegacySettings();

        var lapse = config.FindEngineSettings("lapse")!;
        Assert.Equal("/opt/lapse", lapse.PathOverride);
        Assert.Equal(9, lapse.Penalty);
        Assert.Null(config.EngineBinaryPathOverride);
        Assert.Equal(6, config.DefaultPenalty);
        Assert.Equal(Engines.LapseEngine.DefaultConfidenceSigma, config.ConfidenceSigma);
        Assert.Equal(".shifted", config.SidecarSuffix);
    }

    [Fact]
    public void ABadAlassEncodingIsCleared()
    {
        var config = new PluginConfiguration();
        config.GetEngineSettings("alass").SetParameters(new[]
        {
            new KeyValuePair<string, string?>("encodingRef", "auto"),
            new KeyValuePair<string, string?>("interval", "5")
        });

        config.MigrateLegacySettings();

        var map = config.FindEngineSettings("alass")!.GetParameterMap();
        Assert.Null(map["encodingRef"]);
        Assert.Equal("5", map["interval"]);
    }

    [Fact]
    public void ASnapshotDoesNotFollowTheRecordItWasTakenFrom()
    {
        var record = new MovieSyncRecord { ItemId = Guid.NewGuid(), Status = MovieSyncStatus.Synced };
        record.SyncedSubtitles.Add(new SubtitleSyncRecord { Path = "/m/a.srt" });

        var copy = record.Snapshot();
        record.SyncedSubtitles.Clear();
        record.Status = MovieSyncStatus.Failed;

        Assert.Single(copy.SyncedSubtitles);
        Assert.Equal(MovieSyncStatus.Synced, copy.Status);
        Assert.Equal(record.ItemId, copy.ItemId);
    }

    [Fact]
    public void TheDeferredSaveReachesTheDiskWhenFlushed()
    {
        var plugin = TestHost.Plugin;
        var marker = "lapse-test-" + Guid.NewGuid().ToString("N");
        var before = plugin.Configuration.ExtractPathFilter;

        try
        {
            plugin.Configuration.ExtractPathFilter = marker;
            plugin.SaveConfigurationSoon();
            plugin.FlushDeferredSave();

            Assert.Contains(marker, File.ReadAllText(plugin.ConfigurationFilePath), StringComparison.Ordinal);
        }
        finally
        {
            plugin.Configuration.ExtractPathFilter = before;
            plugin.SaveConfiguration();
        }
    }

    [Theory]

    // daily at 03:00: due in the ten minutes after, not before, not long after
    [InlineData("Daily", null, "03:00", "2026-10-07 03:04", null, true)]
    [InlineData("Daily", null, "03:00", "2026-10-07 02:59", null, false)]
    [InlineData("Daily", null, "03:00", "2026-10-07 03:11", null, false)]

    // already ran an hour ago: not again
    [InlineData("Daily", null, "03:00", "2026-10-07 03:05", -1.0, false)]

    // ran yesterday: due
    [InlineData("Daily", null, "03:00", "2026-10-07 03:05", -24.0, true)]

    // weekly on Wednesday (2026-10-07 is one): due on the day, not on another
    [InlineData("Weekly", DayOfWeek.Wednesday, "21:30", "2026-10-07 21:31", -24 * 7.0, true)]
    [InlineData("Weekly", DayOfWeek.Thursday, "21:30", "2026-10-07 21:31", null, false)]
    [InlineData("Weekly", DayOfWeek.Wednesday, "21:30", "2026-10-07 21:31", -24 * 3.0, false)]

    // an unparseable time falls back to 03:00
    [InlineData("Daily", null, "nonsense", "2026-10-07 03:01", null, true)]
    public void SchedulesFireInTheirSlotAndOnlyOnce(string frequency, DayOfWeek? day, string time, string now, double? lastRunHoursAgo, bool due)
    {
        var library = new LibraryConfig
        {
            ScheduleEnabled = true,
            ScheduleFrequency = Enum.Parse<ScheduleFrequency>(frequency),
            ScheduleDay = day,
            ScheduleTime = time,
            LastScheduledRunUtc = lastRunHoursAgo is { } hours ? DateTime.UtcNow.AddHours(hours) : null
        };

        var local = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(due, LibraryScheduleService.IsDue(library, local));
    }
}
