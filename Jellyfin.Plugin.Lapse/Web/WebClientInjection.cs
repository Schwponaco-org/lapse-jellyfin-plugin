// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
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
    /// The script isn't reaching the web client at all.
    /// </summary>
    None
}

/// <summary>
/// Shared knowledge about getting the LAPSE script into the Jellyfin web client, plus
/// where the last attempt stands.
///
/// This used to be done by editing index.html on disk in the plugin's constructor, the
/// way plugins have done it for years. That quietly fails on most packaged installs -
/// the Debian and RPM packages own /usr/share/jellyfin/web as root while the server runs
/// as the jellyfin user, and the macOS build keeps its web client inside the (signed,
/// read-only) app bundle. The plugin loaded, the dashboard worked, and no sync entry ever
/// appeared in the item menu. So the script is now added to index.html on its way out,
/// which needs no write access anywhere.
/// </summary>
public static class WebClientInjection
{
    /// <summary>
    /// The marker that says the script is already in a page. Also the tag inserted by the
    /// old on-disk patching, so a server whose index.html was successfully edited by an
    /// earlier version doesn't end up with two copies.
    /// </summary>
    public const string Marker = "lapse-inject.js";

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

    /// <summary>
    /// Builds the script and stylesheet tags to insert, rooted at the web client's own
    /// base path so they resolve the same whether the page was loaded as /web/,
    /// /web/index.html, or from behind a configured base URL.
    /// </summary>
    /// <param name="webBasePath">The request path the web client is served under,
    /// e.g. "/web".</param>
    /// <returns>The markup to insert before &lt;/head&gt;.</returns>
    public static string BuildTags(string webBasePath)
    {
        var prefix = webBasePath.TrimEnd('/');

        // data-lapse-inject-css is what the script looks for before adding the stylesheet
        // itself, so tagging it here stops the page ending up with two copies.
        return $"<link rel=\"stylesheet\" href=\"{prefix}/configurationpage?name=lapse-inject.css\" data-lapse-inject-css=\"1\">"
            + $"<script src=\"{prefix}/configurationpage?name=lapse-inject.js\" defer></script>";
    }

    /// <summary>
    /// Puts the tags into a page's head, if they aren't in it already.
    /// </summary>
    /// <param name="html">The page markup.</param>
    /// <param name="webBasePath">The request path the web client is served under.</param>
    /// <returns>The markup with the tags in it, or the original when there was no head
    /// to put them in or they were already there.</returns>
    public static string? Inject(string html, string webBasePath)
    {
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
    /// Says whether a request path is the web client's entry document, which is the only
    /// page worth rewriting. Tolerates a base URL in front of it.
    ///
    /// The trailing slash on the folder form matters and isn't optional here. A request
    /// to "/web" is answered by a redirect to "/web/", and serving the page at the
    /// slashless URL instead would leave the browser resolving every relative script in
    /// it against the site root, which breaks the whole client. So that form is left to
    /// redirect, and we pick the page up on the way back.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>True if this request is for the web client's index page.</returns>
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
    /// <param name="path">The request path that matched <see cref="IsWebIndexPath"/>.</param>
    /// <returns>The base path, without a trailing slash, e.g. "/web".</returns>
    public static string GetWebBasePath(string path)
    {
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^"/index.html".Length];
        }

        return path.TrimEnd('/');
    }

    /// <summary>
    /// Reads the web client's index.html off disk.
    /// </summary>
    /// <param name="webPath">The folder the web client lives in.</param>
    /// <returns>The markup, or null if it couldn't be read.</returns>
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
