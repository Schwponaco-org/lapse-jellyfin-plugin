// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.Lapse.Web;

/// <summary>
/// How the context menu script got into the web client, for the dashboard's status panel.
/// </summary>
public enum InjectionMethod
{
    /// <summary>
    /// The script is added to index.html as it's served, without touching the file.
    /// </summary>
    Middleware,

    /// <summary>
    /// index.html on disk was edited and already carries the script.
    /// </summary>
    PatchedFile,

    /// <summary>
    /// index.html looks fine, the middleware will do its thing once a browser asks for it.
    /// </summary>
    Ready,

    /// <summary>
    /// The script isn't reaching the web client at all.
    /// </summary>
    None
}

/// <summary>
/// Getting our script into the web client, and how that's going.
///
/// We used to edit index.html on disk like plugins traditionally do, but on Debian/RPM the
/// web folder is owned by root and on macOS it's inside the signed app bundle, so it just
/// silently didn't work. Now it's added on the way out by the middleware.
/// </summary>
public static class WebClientInjection
{
    /// <summary>
    /// Says the script is already in a page (old on-disk patching used it too, so no double copies).
    /// </summary>
    public const string Marker = "lapse-inject.js";

    // What the old on-disk patching wrote. No version in it, so browsers would keep a stale
    // cached script forever. We swap it for the versioned one.
    private const string LegacyTag = "<script src=\"configurationpage?name=lapse-inject.js\"></script>";

    /// <summary>
    /// Gets or sets how the script is currently reaching the web client.
    /// </summary>
    public static InjectionMethod Method { get; set; } = InjectionMethod.None;

    /// <summary>
    /// Gets or sets what went wrong, when nothing is reaching the web client.
    /// </summary>
    public static string? Problem { get; set; }

    /// <summary>
    /// Gets or sets the folder the web client is being served from.
    /// </summary>
    public static string? WebPath { get; set; }

    // the assembly doesn't change while running
    private static readonly Lazy<string> Version = new(BuildVersion);

    /// <summary>
    /// Builds the script and stylesheet tags, rooted at the web client's base path (works behind a base URL too).
    /// </summary>
    public static string BuildTags(string webBasePath)
    {
        var prefix = webBasePath.TrimEnd('/');

        // version in the URL so an update actually reaches browsers instead of the cached copy
        var version = Version.Value;

        // the script checks data-lapse-inject-css before adding the stylesheet itself
        return $"<link rel=\"stylesheet\" href=\"{prefix}/configurationpage?name=lapse-inject.css&amp;v={version}\" data-lapse-inject-css=\"1\">"
            + $"<script src=\"{prefix}/configurationpage?name=lapse-inject.js&amp;v={version}\" defer></script>";
    }

    /// <summary>
    /// Cache buster: the assembly version, or its timestamp if there's no version.
    /// </summary>
    private static string BuildVersion()
    {
        var assembly = typeof(WebClientInjection).Assembly;
        var version = assembly.GetName().Version?.ToString();

        if (!string.IsNullOrWhiteSpace(version) && version != "0.0.0.0")
        {
            return version;
        }

        try
        {
            return File.GetLastWriteTimeUtc(assembly.Location)
                .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "0";
        }
    }

    /// <summary>
    /// Puts the tags into a page's head, if they aren't in it already.
    /// </summary>
    public static string? Inject(string html, string webBasePath)
    {
        // swap an old unversioned tag for the current ones
        html = html.Replace(LegacyTag, string.Empty, StringComparison.Ordinal);

        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var headEndIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEndIndex < 0)
        {
            return null;
        }

        return html.Insert(headEndIndex, BuildTags(webBasePath));
    }

    /// <summary>
    /// Says whether a path is the web client's index (base URL in front is fine). This runs on
    /// every request so keep it cheap. "/web" without a slash is left to redirect, serving
    /// the page there breaks every relative URL in it.
    /// </summary>
    public static bool IsWebIndexPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Works out the base path the web client is served under, from the request path.
    /// </summary>
    public static string GetWebBasePath(string path)
    {
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^"/index.html".Length];
        }

        return path.TrimEnd('/');
    }

    /// <summary>
    /// Works out where things stand now. <see cref="Method"/> only moves once a browser loads
    /// index.html, and after a restart the already loaded SPA doesn't, so the dashboard would
    /// show a false warning. If nothing's confirmed yet, check index.html is readable and has a head.
    /// </summary>
    public static InjectionMethod Evaluate()
    {
        if (Method is InjectionMethod.Middleware or InjectionMethod.PatchedFile)
        {
            return Method;
        }

        var html = TryReadIndex(WebPath);
        if (html is null)
        {
            Problem ??= string.IsNullOrWhiteSpace(WebPath)
                ? "this server doesn't expose a web client folder."
                : $"index.html could not be read from {WebPath}.";

            Method = InjectionMethod.None;
            return Method;
        }

        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            Problem = null;
            Method = InjectionMethod.PatchedFile;
            return Method;
        }

        if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
        {
            Problem = null;
            Method = InjectionMethod.Ready;
            return Method;
        }

        Problem = "the web client's index.html has no <head> to add the script to.";
        Method = InjectionMethod.None;
        return Method;
    }

    /// <summary>
    /// Reads the web client's index.html off disk.
    /// </summary>
    public static string? TryReadIndex(string? webPath)
    {
        if (string.IsNullOrWhiteSpace(webPath))
        {
            return null;
        }

        var indexPath = Path.Combine(webPath, "index.html");

        try
        {
            return File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
