// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Lapse.Web;

/// <summary>
/// Adds our script tag to the web client's index.html as it's served (that's what puts the
/// sync entries in the context menu). Doing it here instead of editing the file means it
/// works on packaged Linux/macOS installs where the web folder is read only.
///
/// The request still goes down the rest of the pipeline first, since other plugins (File
/// Transformation, Media Bar, Custom Tabs...) patch the same page there. We add our tags
/// to whatever they produced.
/// </summary>
public class ScriptInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.
    /// </summary>
    public ScriptInjectionMiddleware(
        RequestDelegate next,
        IApplicationPaths applicationPaths,
        ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || !WebClientInjection.IsWebIndexPath(context.Request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();

        // everything downstream writes here instead of to the socket so we can edit it
        context.Response.Body = buffer;

        // no compression, we need to read it. One document per page load, cheap. Put back after.
        var acceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        // A 304 or a range slice is nothing we can add to, and falling back to the disk copy
        // would throw away what other plugins did. So always ask for the full page (we mark
        // it no-store below, so browsers shouldn't send these anyway).
        var conditionals = StripConditionalHeaders(context.Request.Headers);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;

            if (acceptEncoding.Count > 0)
            {
                context.Request.Headers.AcceptEncoding = acceptEncoding;
            }

            foreach (var (name, value) in conditionals)
            {
                context.Request.Headers[name] = value;
            }
        }

        // nothing was sent yet (it all went into the MemoryStream), so headers can still change
        var html = ReadHtmlResponse(context, buffer);

        // nothing usable came back, fall back to the file on disk
        var fromDisk = false;
        if (html is null)
        {
            html = WebClientInjection.TryReadIndex(GetWebPath());
            fromDisk = html is not null;
        }

        var injected = html is null
            ? null
            : WebClientInjection.Inject(html, WebClientInjection.GetWebBasePath(context.Request.Path.Value!));

        if (injected is null)
        {
            // already patched on disk by an old version (fine), or no <head> (leave it alone)
            if (html is not null)
            {
                WebClientInjection.Method = html.Contains(WebClientInjection.Marker, StringComparison.Ordinal)
                    ? InjectionMethod.PatchedFile
                    : InjectionMethod.None;
            }

            await CopyThroughAsync(context, buffer, originalBody).ConfigureAwait(false);
            return;
        }

        if (WebClientInjection.Method != InjectionMethod.Middleware)
        {
            WebClientInjection.Method = InjectionMethod.Middleware;
            WebClientInjection.Problem = null;
            _logger.LogInformation("Serving the Jellyfin web client with the LAPSE context menu script added");
        }

        var bytes = Encoding.UTF8.GetBytes(injected);

        if (fromDisk)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // the page doesn't match the file anymore, drop the validators
        context.Response.Headers.Remove(HeaderNames.ETag);
        context.Response.Headers.Remove(HeaderNames.LastModified);
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static readonly string[] ConditionalHeaders =
    {
        HeaderNames.IfNoneMatch,
        HeaderNames.IfModifiedSince,
        HeaderNames.IfMatch,
        HeaderNames.IfUnmodifiedSince,
        HeaderNames.IfRange,
        HeaderNames.Range
    };

    private static List<(string Name, StringValues Value)> StripConditionalHeaders(IHeaderDictionary headers)
    {
        var removed = new List<(string, StringValues)>();

        foreach (var name in ConditionalHeaders)
        {
            if (headers.TryGetValue(name, out var value))
            {
                removed.Add((name, value));
                headers.Remove(name);
            }
        }

        return removed;
    }

    // The page as text, or null for anything we shouldn't touch (redirect, 404, compressed body).
    private static string? ReadHtmlResponse(HttpContext context, MemoryStream buffer)
    {
        if (context.Response.StatusCode != StatusCodes.Status200OK
            || buffer.Length == 0
            || context.Response.Headers.ContentEncoding.Count > 0)
        {
            return null;
        }

        var contentType = context.Response.ContentType;
        if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.Contains("</head>", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    private static async Task CopyThroughAsync(HttpContext context, MemoryStream buffer, Stream originalBody)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private string? GetWebPath()
    {
        if (WebClientInjection.WebPath is not null)
        {
            return WebClientInjection.WebPath;
        }

        try
        {
            WebClientInjection.WebPath = _applicationPaths.WebPath;
        }
        catch (NotSupportedException)
        {
            // no web client on this host
            WebClientInjection.Problem = "This server doesn't expose a web client folder.";
        }

        return WebClientInjection.WebPath;
    }
}

/// <summary>
/// Puts <see cref="ScriptInjectionMiddleware"/> at the front of the pipeline. Plugins can't
/// touch Startup, but an IStartupFilter from the registrator gets picked up early enough.
/// </summary>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            builder.UseMiddleware<ScriptInjectionMiddleware>();
            next(builder);
        };
    }
}
