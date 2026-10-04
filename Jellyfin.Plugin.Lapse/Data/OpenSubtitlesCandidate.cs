// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Collections.Generic;

namespace Jellyfin.Plugin.Lapse.Data;

/// <summary>
/// One subtitle OpenSubtitles offered for an item.
/// </summary>
public class OpenSubtitlesCandidate
{
    /// <summary>
    /// Gets or sets the id OpenSubtitles downloads the file by.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Gets or sets the file's own name, which says what format it is in.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles language code: "en", "da", "pt-br".
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release the uploader made it for, as they typed it.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Gets or sets how many times it has been downloaded.
    /// </summary>
    public int DownloadCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it was made for this exact file: its hash
    /// matches the video's. Those are the ones most likely to be right before any sync.
    /// </summary>
    public bool HashMatch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a trusted uploader sent it in.
    /// </summary>
    public bool FromTrusted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it carries sound descriptions for the deaf
    /// and hard of hearing.
    /// </summary>
    public bool HearingImpaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it only covers the foreign language parts,
    /// what Jellyfin calls forced.
    /// </summary>
    public bool ForeignPartsOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a machine or an AI translated it.
    /// </summary>
    public bool Translated { get; set; }

    /// <summary>
    /// Gets or sets the title OpenSubtitles has it under, to catch a match on the wrong
    /// film before it's downloaded.
    /// </summary>
    public string? Title { get; set; }
}

/// <summary>
/// What a search for an item's subtitles found.
/// </summary>
public class OpenSubtitlesSearchResult
{
    /// <summary>
    /// Gets the subtitles found, best first.
    /// </summary>
    public List<OpenSubtitlesCandidate> Candidates { get; } = new();

    /// <summary>
    /// Gets or sets what the search went on: the file's hash, its IMDb or TMDb id, its
    /// title, or its file name.
    /// </summary>
    public string? SearchedBy { get; set; }

    /// <summary>
    /// Gets or sets why the search failed, when it did. An empty result with no error
    /// just means OpenSubtitles has nothing.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// What OpenSubtitles said about the account, for the settings page's test button.
/// </summary>
public class OpenSubtitlesAccountStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether everything needed to search and download
    /// works.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a line saying how it went.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many downloads the account has left today, when it said.
    /// </summary>
    public int? RemainingDownloads { get; set; }

    /// <summary>
    /// Gets or sets how many downloads a day the account gets, when it said.
    /// </summary>
    public int? AllowedDownloads { get; set; }
}

/// <summary>
/// Credentials to test before they're saved. Anything left empty falls back to what's
/// saved, so the password box doesn't have to be filled in again just to test.
/// </summary>
public class OpenSubtitlesTestRequest
{
    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the account name.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>
/// One subtitle picked from a search, to download for an item.
/// </summary>
public class OpenSubtitlesDownloadRequest
{
    /// <summary>
    /// Gets or sets the file id from the search.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Gets or sets the language it's in, from the search.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the file's name from the search, for its format.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's a hearing impaired subtitle, so the
    /// file can be named the way Jellyfin recognises.
    /// </summary>
    public bool HearingImpaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it only covers the foreign parts.
    /// </summary>
    public bool ForeignPartsOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to sync it straight after, with the default
    /// engine.
    /// </summary>
    public bool Sync { get; set; }
}
