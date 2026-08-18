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
/// Serves the web client's index.html with the LAPSE script tag added. This is what puts
/// the sync entries in the item context menu, and doing it here rather than by editing
/// the file means it works on the packaged Linux and macOS builds, where the web folder
/// isn't writable by the account the server runs as.
///
/// The request is always passed down the rest of the pipeline first and the page comes
/// back out of whatever answered it, rather than being read off disk and returned from
/// here. That matters because other plugins - anything built on File Transformation,
/// Media Bar and Custom Tabs among them - patch the same page further down. Answering
/// here and returning would mean they never run at all, so this waits for their version
/// of the page and adds its tags to that.
/// </summary>
public class ScriptInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="applicationPaths">Used to find the web client folder.</param>
    /// <param name="logger">Logger.</param>
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
    /// <param name="context">The request.</param>
    /// <returns>Task.</returns>
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

        // Everything downstream - the static file handler, and any plugin that has hooked
        // it - writes in here instead of straight out to the socket, so the page can be
        // read back and added to before it goes anywhere.
        context.Response.Body = buffer;

        // Asking for a compressed response would leave us holding bytes we can't read or
        // rewrite. This is one document per page load, so giving up the compression on it
        // costs little. The header goes back afterwards in case anything else looks at it.
        var acceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        // A conditional request is answered with an empty 304, and a range request with a
        // slice, and neither is a page we can add to. Falling back to the copy on disk at
        // that point would throw away whatever File Transformation and the plugins built
        // on it did further down, so the conditions come off and the full page is asked
        // for every time. The response is marked no-store below, so a browser that has
        // been here before won't be sending these anyway.
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

        // Headers were never sent, since the body they'd have gone with was a
        // MemoryStream, so there's still a free hand to change the response here.
        var html = ReadHtmlResponse(context, buffer);

        // Nothing downstream produced a page we can work with. Falling back to the copy on
        // disk keeps this working on setups where something else answers the request in a
        // way we can't read, at the cost of dropping whatever that something else did.
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
            // Either an older version successfully patched index.html on disk, or there's
            // no head to insert into. The first is fine and already does the job; the
            // second means this page isn't what we thought it was, so leave it alone.
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

        // The page no longer matches whatever was on disk, so the validators that came
        // with it would have a client caching the wrong thing.
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

    // Gets what the pipeline produced as text, or null when it isn't an HTML page this
    // should be touching - a redirect, a 404, a compressed or otherwise encoded body.
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
            // A host that doesn't serve a web client at all has nothing for us to patch.
            WebClientInjection.Problem = "This server doesn't expose a web client folder.";
        }

        return WebClientInjection.WebPath;
    }
}

/// <summary>
/// Puts <see cref="ScriptInjectionMiddleware"/> at the front of Jellyfin's request
/// pipeline. Plugins don't get to edit Startup, but an IStartupFilter registered from
/// the service registrator is picked up by the generic host when it builds the pipeline,
/// which is early enough to answer for index.html before the static file handler does.
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
