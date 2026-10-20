// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using Jellyfin.Plugin.Lapse.Services.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// Translation from the file inwards: which lines count as dialogue, batching, and a
/// whole job run against a provider that answers however a test tells it to.
/// </summary>
public class TranslationTests
{
    public TranslationTests()
    {
        _ = TestHost.Plugin;
    }

    private static async Task<SubtitleTextFile> Load(string name, string text)
    {
        var path = Path.Combine(TestHost.NewFolder(), name);
        await File.WriteAllTextAsync(path, text);
        return await SubtitleTextFile.LoadAsync(path);
    }

    [Fact]
    public async Task VttHeadersNotesAndStyleBlocksAreNotDialogue()
    {
        var file = await Load("Film.en.vtt", """
            WEBVTT

            STYLE
            ::cue { color: yellow }

            REGION
            id:fred width:40%

            NOTE a comment
            spanning two lines

            intro
            00:01.000 --> 00:02.000
            Hello there

            00:03.000 --> 00:04.000 align:start
            1984
            was a year
            """);

        Assert.Equal(new[] { "Hello there", "1984", "was a year" }, file.TextLines);
    }

    [Fact]
    public async Task CueNumbersAreSkippedEvenWithoutBlankLinesBetweenCues()
    {
        var file = await Load("Film.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nFirst\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n42\n\n3\n00:00:05,000 --> 00:00:06,000\nThird\n");

        // The 42 is a line of dialogue: it isn't followed by a timing line.
        Assert.Equal(new[] { "First", "Second", "42", "Third" }, file.TextLines);
    }

    [Fact]
    public async Task AssDialogueKeepsItsFieldsAndCommasInTheText()
    {
        var file = await Load("Film.en.ass", "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Well, hello, there\nComment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,not this\n");

        Assert.Equal(new[] { "Well, hello, there" }, file.TextLines);

        file.ApplyTranslations(new[] { "Nå, hej, du" });
        var output = Path.Combine(TestHost.NewFolder(), "Film.da.ass");
        await file.SaveAsync(output, new[] { "Translated by a test" });

        var written = await File.ReadAllTextAsync(output);
        Assert.StartsWith("; Translated by a test", written, StringComparison.Ordinal);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Nå, hej, du", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/m/Film.en.srt", "da", "/m/Film.da.translated.srt")]
    [InlineData("/m/Film.da.translated.srt", "es", "/m/Film.es.translated.srt")]
    [InlineData("/m/Film.srt", "pt-BR", "/m/Film.pt-BR.translated.srt")]
    [InlineData("/m/Film.en.srt", "../../etc", "/m/Film.etc.translated.srt")]
    public void TranslatedFilesAreNamedAfterTheirLanguage(string source, string language, string expected)
    {
        Assert.Equal(expected, SubtitleTextFile.BuildOutputPath(source, language));
    }

    [Fact]
    public void BatchesCoverEveryLineInOrder()
    {
        var lines = Enumerable.Range(0, 7).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var batches = TranslationBatches.Split(lines, 3).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal(lines, batches.SelectMany(b => b));
        Assert.Single(batches[^1]);
    }

    [Fact]
    public void AnswersAreCutOrPaddedToTheBatch()
    {
        var tooMany = new List<TranslatedLine> { new() { Text = "a" }, new() { Text = "b" }, new() { Text = "c" } };
        var tooFew = new List<TranslatedLine> { new() { Text = "a" } };

        Assert.Equal(new[] { "a", "b" }, TranslationBatches.Align(tooMany, 2, NullLogger.Instance, "Test").Select(t => t.Text));
        Assert.Equal(new[] { "a", null }, TranslationBatches.Align(tooFew, 2, NullLogger.Instance, "Test").Select(t => t.Text));
    }

    [Fact]
    public async Task AJobTranslatesTheDialogueAndLeavesTheTimingsAlone()
    {
        var folder = TestHost.NewFolder();
        var source = Path.Combine(folder, "Film.en.srt");
        await File.WriteAllTextAsync(source, "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nGoodbye\n");

        var service = new TranslationService(new[] { new FakeProvider(lines => lines.Select(l => "[" + l + "]")) }, NullLogger<TranslationService>.Instance);

        var result = await service.TranslateAsync(source, new TranslationRequest
        {
            SubtitlePath = source,
            TargetLanguage = "da",
            Provider = TranslationProvider.MyMemory,
            ConfidenceThreshold = 0,
            IncludeMetadataHeader = false
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.TranslatedCount);
        Assert.Equal(Path.Combine(folder, "Film.da.translated.srt"), result.OutputPath);
        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:02,000\n[Hello]\n\n2\n00:00:03,000 --> 00:00:04,000\n[Goodbye]\n",
            (await File.ReadAllTextAsync(result.OutputPath!)).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProviderAnsweringWithSomethingElseIsAFailedJobNotACrash()
    {
        var source = Path.Combine(TestHost.NewFolder(), "Film.en.srt");
        await File.WriteAllTextAsync(source, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        var service = new TranslationService(
            new[] { new FakeProvider(_ => throw new System.Text.Json.JsonException("'<' is an invalid start of a value")) },
            NullLogger<TranslationService>.Instance);

        var result = await service.TranslateAsync(source, new TranslationRequest { SubtitlePath = source, TargetLanguage = "da", Provider = TranslationProvider.MyMemory });

        Assert.False(result.Success);
        Assert.Contains("isn't a translation", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "Film.da.translated.srt")));
    }

    [Fact]
    public async Task NoTargetLanguageIsTurnedAwayBeforeAnythingIsSent()
    {
        var provider = new FakeProvider(lines => lines);
        var service = new TranslationService(new[] { provider }, NullLogger<TranslationService>.Instance);

        var result = await service.TranslateAsync("/nowhere.srt", new TranslationRequest { TargetLanguage = " " });

        Assert.False(result.Success);
        Assert.Equal(0, provider.Calls);
    }

    private sealed class FakeProvider : ITranslationProvider
    {
        private readonly Func<IReadOnlyList<string>, IEnumerable<string>> _translate;

        public FakeProvider(Func<IReadOnlyList<string>, IEnumerable<string>> translate)
        {
            _translate = translate;
        }

        public int Calls { get; private set; }

        public TranslationProvider Id => TranslationProvider.MyMemory;

        public string DisplayName => "Fake";

        public int Tier => 0;

        public string Summary => "For tests.";

        public string? GetConfigurationProblem() => null;

        public Task<IReadOnlyList<TranslatedLine>> TranslateAsync(IReadOnlyList<string> lines, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<TranslatedLine> result = _translate(lines).Select(t => new TranslatedLine { Text = t, Confidence = 1 }).ToList();
            return Task.FromResult(result);
        }
    }
}
