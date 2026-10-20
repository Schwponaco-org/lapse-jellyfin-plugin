// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Reflection;
using Jellyfin.Plugin.Lapse.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// Finding an item's subtitles on disk and in Jellyfin's own record of it. The record
/// comes from a stand-in for Jellyfin's media source manager, the files from a real
/// folder laid out the way libraries are.
/// </summary>
public class SubtitleLocatorTests
{
    public SubtitleLocatorTests()
    {
        _ = TestHost.Plugin;
        BaseItem.MediaSourceManager = StreamsProxy.Create();
    }

    private static Movie Item(string path, params MediaStream[] streams)
    {
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        StreamsProxy.Streams[item.Id] = streams.ToList();
        return item;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        return path;
    }

    [Fact]
    public void AnEpisodeGetsItsOwnSubtitlesAndNotTheSeasons()
    {
        var season = TestHost.NewFolder();
        Touch(Path.Combine(season, "Show S01E01.mkv"));
        Touch(Path.Combine(season, "Show S01E02.mkv"));

        var own = Touch(Path.Combine(season, "Show.S01E01.en.srt"));
        Touch(Path.Combine(season, "Show S01E02.en.srt"));
        Touch(Path.Combine(season, "English.srt"));
        Touch(Path.Combine(season, "Show S01E01.en.srt.lapse-tmp.srt"));
        var inSubs = Touch(Path.Combine(season, "Subs", "Show S01E01.da.srt"));
        Touch(Path.Combine(season, "Subs", "Show S01E02.da.srt"));
        var inOwnFolder = Touch(Path.Combine(season, "Show S01E01", "Subtitles", "whatever.srt"));
        var looseInOwnFolder = Touch(Path.Combine(season, "Show S01E01", "forced.srt"));

        var item = Item(Path.Combine(season, "Show S01E01.mkv"));
        var found = new SubtitleLocator().GetExternalSubtitles(item).Select(s => s.Path).ToList();

        Assert.Equal(
            new[] { own, inSubs, inOwnFolder, looseInOwnFolder }.OrderBy(p => p, StringComparer.Ordinal),
            found.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void ALoneVideoTakesWhateverSubtitleIsBesideIt()
    {
        var folder = TestHost.NewFolder();
        Touch(Path.Combine(folder, "Film.mkv"));
        var loose = Touch(Path.Combine(folder, "English.srt"));
        Touch(Path.Combine(folder, "Film.nfo"));

        var found = new SubtitleLocator().GetExternalSubtitles(Item(Path.Combine(folder, "Film.mkv")));

        Assert.Equal(new[] { loose }, found.Select(s => s.Path));
        Assert.True(found[0].Supported);
        Assert.Equal("srt", found[0].Format);
    }

    [Fact]
    public void TracksJellyfinKnowsAreListedOnceAndEmbeddedOnesAsWell()
    {
        var folder = TestHost.NewFolder();
        Touch(Path.Combine(folder, "Film.mkv"));
        var external = Touch(Path.Combine(folder, "Film.en.srt"));

        var item = Item(
            Path.Combine(folder, "Film.mkv"),
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Path = external, Language = "eng", IsExternal = true },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1 },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip", Language = "dan" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Codec = "hdmv_pgs_subtitle", Language = "swe" });

        var found = new SubtitleLocator().GetExternalSubtitles(item);

        Assert.Equal(3, found.Count);
        Assert.Equal("eng (Film.en.srt)", found[0].DisplayName);
        Assert.Equal("embedded://2", found[1].Path);
        Assert.True(found[1].IsEmbedded);
        Assert.True(found[1].Supported);
        Assert.Equal("embedded://3", found[2].Path);
        Assert.False(found[2].Supported);
    }

    [Fact]
    public void ASubtitleDeletedSinceTheLastScanIsNotOffered()
    {
        var folder = TestHost.NewFolder();
        Touch(Path.Combine(folder, "Film.mkv"));
        var gone = Path.Combine(folder, "Film.en.srt");

        var item = Item(
            Path.Combine(folder, "Film.mkv"),
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Path = gone, Language = "eng", IsExternal = true });

        Assert.Empty(new SubtitleLocator().GetExternalSubtitles(item));
    }

    [Fact]
    public void AFileWrittenAfterTheFirstLookIsFoundOnTheNext()
    {
        var folder = TestHost.NewFolder();
        Touch(Path.Combine(folder, "Film.mkv"));
        var locator = new SubtitleLocator();
        var item = Item(Path.Combine(folder, "Film.mkv"));

        Assert.Empty(locator.GetExternalSubtitles(item));

        var added = Touch(Path.Combine(folder, "Film.en.shifted.srt"));

        // The folder's own write time moved, so the remembered listing is not used.
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(new[] { added }, locator.GetExternalSubtitles(item).Select(s => s.Path));
    }

    /// <summary>
    /// Answers GetMediaStreams from a table and everything else with nothing.
    /// </summary>
    public class StreamsProxy : DispatchProxy
    {
        public static Dictionary<Guid, List<MediaStream>> Streams { get; } = new();

        public static IMediaSourceManager Create() => Create<IMediaSourceManager, StreamsProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            if (targetMethod.Name == nameof(IMediaSourceManager.GetMediaStreams))
            {
                var id = args?[0] switch
                {
                    MediaStreamQuery query => query.ItemId,
                    Guid guid => guid,
                    _ => Guid.Empty
                };

                return Streams.TryGetValue(id, out var streams) ? new List<MediaStream>(streams) : new List<MediaStream>();
            }

            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
