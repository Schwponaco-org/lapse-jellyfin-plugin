// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Text;
using Jellyfin.Plugin.Lapse.Data;
using Jellyfin.Plugin.Lapse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lapse.Tests;

public class SubtitleTimingTests
{
    public SubtitleTimingTests()
    {
        _ = TestHost.Plugin;
    }

    [Theory]
    [InlineData("00:00:01,000 --> 00:00:02,500", 1234, "00:00:02,234 --> 00:00:03,734")]
    [InlineData("01:23.456 --> 01:25.000", 1000, "01:24.456 --> 01:26.000")]
    [InlineData("59:59.500 --> 59:59.900", 1000, "01:00:00.500 --> 01:00:00.900")]
    [InlineData("0:00:01.00,0:00:02.00", 1234, "0:00:02.23,0:00:03.23")]
    [InlineData("0:00:01.00,0:00:02.00", 1235, "0:00:02.24,0:00:03.24")]
    [InlineData("0:00:59.99,0:01:00.00", 6, "0:01:00.00,0:01:00.01")]
    public void PreviewShiftRoundsAndKeepsTheShape(string line, int offsetMs, string expected)
    {
        Assert.Equal(expected, SubtitleShifter.PreviewShift(line, offsetMs));
    }

    [Fact]
    public void ACuePushedBeforeZeroKeepsItsLength()
    {
        // 1.0 to 3.0, moved back 1.5s: it starts at zero and is still two seconds long,
        // rather than being cut down to half a second.
        Assert.Equal("00:00:00,000 --> 00:00:02,000", SubtitleShifter.PreviewShift("00:00:01,000 --> 00:00:03,000", -1500));

        // Wholly before zero used to pin both ends to zero and leave nothing on screen.
        Assert.Equal("00:00:00,000 --> 00:00:02,000", SubtitleShifter.PreviewShift("00:00:01,000 --> 00:00:03,000", -9000));
    }

    [Fact]
    public async Task ShiftKeepsEncodingLineEndingsAndTheLastLine()
    {
        var folder = TestHost.NewFolder();
        var path = Path.Combine(folder, "Film.ru.srt");
        var text = "1\r\n00:00:05,000 --> 00:00:06,000\r\nПривет\r\n\r\n2\r\n00:00:07,000 --> 00:00:08,500\r\nМир\r\n";
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = System.Text.Encoding.GetEncoding(1251);
        await File.WriteAllBytesAsync(path, cp1251.GetBytes(text));

        var shifter = new SubtitleShifter();
        var result = await shifter.ShiftAsync(path, -1.0, OutputMode.SidecarOnly);

        var bytes = await File.ReadAllBytesAsync(result.OutputPath);
        Assert.Equal(cp1251.GetBytes(text.Replace("00:00:05,000", "00:00:04,000").Replace("00:00:06,000", "00:00:05,000").Replace("00:00:07,000", "00:00:06,000").Replace("00:00:08,500", "00:00:07,500")), bytes);
        Assert.Equal(4, result.Shifted);
    }

    [Fact]
    public async Task ShiftKeepsAByteOrderMark()
    {
        var folder = TestHost.NewFolder();
        var path = Path.Combine(folder, "Film.en.srt");
        await File.WriteAllTextAsync(path, "1\n00:00:05,000 --> 00:00:06,000\nHi\n", new UTF8Encoding(true));

        var result = await new SubtitleShifter().ShiftAsync(path, 0.5, OutputMode.OverwriteNoBackup);
        var bytes = await File.ReadAllBytesAsync(result.OutputPath);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("1\n00:00:05,500 --> 00:00:06,500\nHi\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task ShiftMovesVttInlineTimestampsAndHourlessTimes()
    {
        var folder = TestHost.NewFolder();
        var path = Path.Combine(folder, "Film.en.vtt");
        await File.WriteAllTextAsync(path, "WEBVTT\n\n00:01.000 --> 00:04.000\nOne <00:02.000>two <00:03.000>three\n");

        var result = await new SubtitleShifter().ShiftAsync(path, 2.0, OutputMode.SidecarOnly);

        Assert.Equal(
            "WEBVTT\n\n00:03.000 --> 00:06.000\nOne <00:04.000>two <00:05.000>three\n",
            await File.ReadAllTextAsync(result.OutputPath));
    }

    [Fact]
    public async Task ShiftOnlyTouchesAssTimingFields()
    {
        var folder = TestHost.NewFolder();
        var path = Path.Combine(folder, "Film.en.ass");
        await File.WriteAllTextAsync(path, "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,At 0:00:09.00 we leave\n");

        var result = await new SubtitleShifter().ShiftAsync(path, 1.004, OutputMode.SidecarOnly);

        Assert.Contains("Dialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,At 0:00:09.00 we leave", await File.ReadAllTextAsync(result.OutputPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsHourlessVttToSrtInTimeOrderWithoutVttMarkup()
    {
        var folder = TestHost.NewFolder();
        var source = Path.Combine(folder, "Film.en.vtt");
        var destination = Path.Combine(folder, "Film.en.srt");
        await File.WriteAllTextAsync(source, "WEBVTT\n\n00:05.000 --> 00:06.000\n<v Anna>Second &amp; last</v>\n\n00:01.000 --> 00:02.000\n<c.yellow>First</c> <00:01.500>word\n");

        var converter = new SubtitleConverter(null!, NullLogger<SubtitleConverter>.Instance);
        var cues = await converter.ConvertAsync(source, destination);

        Assert.Equal(2, cues);
        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:02,000\nFirst word\n\n2\n00:00:05,000 --> 00:00:06,000\nSecond & last\n\n",
            await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task ConvertRoundsAssCentiseconds()
    {
        var folder = TestHost.NewFolder();
        var source = Path.Combine(folder, "Film.en.srt");
        var destination = Path.Combine(folder, "Film.en.ass");
        await File.WriteAllTextAsync(source, "1\n00:00:01,005 --> 00:00:59,996\nHello\n");

        await new SubtitleConverter(null!, NullLogger<SubtitleConverter>.Instance).ConvertAsync(source, destination);

        Assert.Contains("Dialogue: 0,0:00:01.01,0:01:00.00,Default,,0,0,0,,Hello", await File.ReadAllTextAsync(destination), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyCodePageIsDetectedAndKept()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = System.Text.Encoding.GetEncoding(1256).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nمرحبا بالعالم\n");

        var (text, encoding) = SubtitleEncoding.DecodeWithEncoding(bytes);

        Assert.Contains("مرحبا", text, StringComparison.Ordinal);
        Assert.Equal(1256, encoding.CodePage);
    }
}
