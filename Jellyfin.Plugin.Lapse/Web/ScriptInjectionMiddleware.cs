// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lapse.Web;

/// <summary>
/// Serves the web client's index.html with the LAPSE script tag added, instead of the
/// copy on disk. This is what puts the sync entries in the item context menu, and doing
/// it here rather than by editing the file means it works on the packaged Linux and macOS
/// builds, where the web folder isn't writable by the account the server runs as.
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

        var html = WebClientInjection.TryReadIndex(GetWebPath());
        if (html is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var basePath = WebClientInjection.GetWebBasePath(context.Request.Path.Value!);
        var injected = WebClientInjection.Inject(html, basePath);

        if (injected is null)
        {
            // Either an older version successfully patched index.html on disk, or there's
            // no head to insert into. The first is fine and already does the job; the
            // second means this page isn't what we thought it was, so leave it alone.
            WebClientInjection.Method = html.Contains(WebClientInjection.Marker, StringComparison.Ordinal)
                ? InjectionMethod.PatchedFile
                : InjectionMethod.None;

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (WebClientInjection.Method != InjectionMethod.Middleware)
        {
            WebClientInjection.Method = InjectionMethod.Middleware;
            WebClientInjection.Problem = null;
            _logger.LogInformation("Serving the Jellyfin web client with the LAPSE context menu script added");
        }

        var bytes = Encoding.UTF8.GetBytes(injected);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // The page differs from the file on disk, so nothing downstream should be handing
        // out a cached copy of the original.
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
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
