// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Services.Translation;

/// <summary>
/// Shared batching bits: split lines into requests and make sure answers line up one to one.
/// </summary>
public static class TranslationBatches
{
    /// <summary>
    /// Cuts lines into batches of at most a given size, in order.
    /// </summary>
    public static IEnumerable<List<string>> Split(IReadOnlyList<string> lines, int size)
    {
        for (var start = 0; start < lines.Count; start += size)
        {
            var batch = new List<string>(Math.Min(size, lines.Count - start));
            for (var i = start; i < Math.Min(start + size, lines.Count); i++)
            {
                batch.Add(lines[i]);
            }

            yield return batch;
        }
    }

    /// <summary>
    /// Pads or trims a batch's answers to the batch size, so one missing answer doesn't shift
    /// every following line by one.
    /// </summary>
    public static List<TranslatedLine> Align(List<TranslatedLine> results, int expected, ILogger logger, string provider)
    {
        if (results.Count == expected)
        {
            return results;
        }

        logger.LogWarning("{Provider} returned {Got} translations for {Expected} lines", provider, results.Count, expected);

        if (results.Count > expected)
        {
            results.RemoveRange(expected, results.Count - expected);
        }

        while (results.Count < expected)
        {
            results.Add(new TranslatedLine());
        }

        return results;
    }

    /// <summary>
    /// Cuts an error body down to something that fits in a message.
    /// </summary>
    public static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
