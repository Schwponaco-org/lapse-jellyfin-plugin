// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Services;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// The file level pieces: taking turns on a file, the names extracted and fetched
/// subtitles get and how those names are read back, and ffprobe's answer.
/// </summary>
public class SubtitleFileTests
{
    [Fact]
    public async Task JobsOnOneFileTakeTurnsAndTheGateGoesAwayAfter()
    {
        var path = Path.Combine(TestHost.NewFolder(), "Film.en.srt");

        var first = await SubtitleFileLock.AcquireAsync(path);
        var second = SubtitleFileLock.AcquireAsync(path);

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second).Dispose();

        // nothing holds it or waits on it, so it's gone
        Assert.False(SubtitleFileLock.HasGate(path));
    }

    [Fact]
    public async Task GivingUpOnTheWaitLeavesNothingBehind()
    {
        var path = Path.Combine(TestHost.NewFolder(), "Film.en.srt");
        var held = await SubtitleFileLock.AcquireAsync(path);

        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubtitleFileLock.AcquireAsync(path, cancel.Token));

        held.Dispose();
        held.Dispose();

        Assert.False(SubtitleFileLock.HasGate(path));
        (await SubtitleFileLock.AcquireAsync(path)).Dispose();
    }

    [Fact]
    public async Task WritingGoesThroughATemporaryFile()
    {
        var folder = TestHost.NewFolder();
        var path = Path.Combine(folder, "Film.en.srt");
        await File.WriteAllTextAsync(path, "old");

        await SubtitleFileLock.WriteTextAsync(path, "new ✓");

        Assert.Equal("new ✓", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("hi", "hin")]
    [InlineData("forced", "und")]
    [InlineData("../../etc", "und")]
    [InlineData(null, "und")]
    [InlineData("", "und")]
    public void ALanguageTagOnlyReachesAFileNameAsATag(string? language, string expected)
    {
        Assert.Equal(expected, ExtractedSubtitleNames.LanguageTag(language));
    }

    [Fact]
    public void AnExtractedTrackIsNamedSoItCanBeFoundAgain()
    {
        var path = ExtractedSubtitleNames.BuildPath("/m/Film (2020).mkv", 8, "eng", forced: true, hearingImpaired: false, ".srt");

        Assert.Equal(Path.Combine("/m", "Film (2020).eng.forced.stream8.srt"), path);
        Assert.True(ExtractedSubtitleNames.IsExtractedTrack("/m/Film (2020).mkv", path));
        Assert.False(ExtractedSubtitleNames.IsExtractedTrack("/m/Film (2020).mkv", "/m/Film (2020).eng.track8.srt"));
        Assert.False(ExtractedSubtitleNames.IsExtractedTrack("/m/Film (2020).mkv", "/other/Film (2020).eng.stream8.srt"));
        Assert.False(ExtractedSubtitleNames.IsExtractedTrack("/m/Film (2020).mkv", "/m/Film (2020).eng.srt"));
        Assert.True(ExtractedSubtitleNames.FitsFileSystem(path));
        Assert.False(ExtractedSubtitleNames.FitsFileSystem("/m/" + new string('x', 250) + ".srt"));
    }

    [Theory]
    [InlineData("Film.en.srt", "en", false)]
    [InlineData("Film.en.forced.srt", "en", true)]
    [InlineData("Film.English.sdh.srt", "English", false)]
    [InlineData("Film.hi.srt", "hi", false)]
    [InlineData("Film.da.hi.srt", "da", false)]
    [InlineData("Film.foreign.srt", null, true)]
    [InlineData("Other.en.srt", null, false)]
    public void AFileNameIsReadTheWayJellyfinReadsIt(string file, string? language, bool forced)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "da", "hi", "English" };

        var (gotLanguage, gotForced) = ExistingSubtitleNames.Parse("/m/Film.mkv", "/m/" + file, known.Contains);

        Assert.Equal(language, gotLanguage);
        Assert.Equal(forced, gotForced);
    }

    [Theory]
    [InlineData("English (Forced)", true, false)]
    [InlineData("Non-Forced", false, false)]
    [InlineData("no forced", false, false)]
    [InlineData("Unforced", false, false)]
    [InlineData("English SDH", false, true)]
    [InlineData("without SDH", false, false)]
    [InlineData("Closed Captions", false, true)]
    [InlineData(null, false, false)]
    public void ATrackTitleIsReadForWhatItMeant(string? title, bool forced, bool hearingImpaired)
    {
        Assert.Equal(forced, SubtitleTrackFlags.TitleSaysForced(title));
        Assert.Equal(hearingImpaired, SubtitleTrackFlags.TitleSaysHearingImpaired(title));
    }

    [Fact]
    public void FfprobesAnswerIsRead()
    {
        var tracks = EmbeddedSubtitleProbe.Parse("""
            {"streams":[
              {"index":2,"codec_name":"subrip","tags":{"LANGUAGE":"eng","title":"English (Forced)"},"disposition":{"forced":0,"hearing_impaired":0}},
              {"index":3,"codec_name":"hdmv_pgs_subtitle","tags":{"language":"dan"},"disposition":{"forced":1}},
              {"index":"x","codec_name":"ass"}
            ]}
            """)!;

        Assert.Equal(2, tracks.Count);
        Assert.Equal("eng", tracks[0].Language);
        Assert.True(tracks[0].IsForced);
        Assert.False(tracks[0].ForcedFlag);
        Assert.True(tracks[1].ForcedFlag);

        Assert.Empty(EmbeddedSubtitleProbe.Parse("{}")!);
        Assert.Null(EmbeddedSubtitleProbe.Parse("not json"));
    }

    [Fact]
    public void FormatsAreSortedIntoWhatCanBeDoneWithThem()
    {
        Assert.Equal(SubtitleFormatKind.Native, SubtitleFormats.GetKind("/m/a.SRT"));
        Assert.Equal(SubtitleFormatKind.Convertible, SubtitleFormats.GetKind("/m/a.sub"));
        Assert.Equal(SubtitleFormatKind.ImageBased, SubtitleFormats.GetKind("/m/a.sup"));
        Assert.Equal(SubtitleFormatKind.Unknown, SubtitleFormats.GetKind("/m/a.nfo"));
        Assert.True(SubtitleFormats.TryNormalizeOutputFormat(".VTT", out var vtt));
        Assert.Equal("vtt", vtt);
        Assert.False(SubtitleFormats.TryNormalizeOutputFormat("sub", out _));
    }
}
