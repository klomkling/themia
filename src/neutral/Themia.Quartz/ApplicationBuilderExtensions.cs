using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Quartz;
using Themia.Quartz;
using Themia.Quartz.Dashboard;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application-builder entry points for the Themia Quartz dashboard. Ported from SilkierQuartz's
/// <c>UseSilkierQuartz</c>, minus the dropped cookie-authentication route and <c>[SilkierQuartz]</c>
/// job auto-discovery. Authorization is enforced by the host-supplied
/// <see cref="ThemiaQuartzOptions.Authorize"/> gate.
/// </summary>
public static class ThemiaQuartzApplicationBuilderExtensions
{
    /// <summary>
    /// Maps the Themia Quartz dashboard endpoints under <see cref="ThemiaQuartzOptions.VirtualPathRoot"/>
    /// and, when <paramref name="endpoints"/> is also an <see cref="IApplicationBuilder"/> (as
    /// <c>WebApplication</c> is), wires the per-request middleware (deny-all authorize gate, dashboard
    /// <see cref="Services"/> bridge, embedded content static files, execution-history store bridge).
    /// </summary>
    /// <remarks>
    /// For hosts that separate the middleware pipeline from endpoint routing, call
    /// <see cref="UseThemiaQuartz(IApplicationBuilder)"/> on the application builder and then
    /// <see cref="MapThemiaQuartz(IEndpointRouteBuilder)"/> inside <c>UseEndpoints</c>; the middleware
    /// is registered only once even if both are called.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map the dashboard routes on.</param>
    /// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapThemiaQuartz(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints is IApplicationBuilder app)
        {
            app.UseThemiaQuartz();
        }

        var options = endpoints.ServiceProvider.GetRequiredService<ThemiaQuartzOptions>();
        var root = NormalizeRoot(options.VirtualPathRoot);
        endpoints.MapControllerRoute(
            "ThemiaQuartz",
            $"{root}{{controller=Scheduler}}/{{action=Index}}");

        return endpoints;
    }

    /// <summary>
    /// Registers the Themia Quartz dashboard request middleware: the deny-all
    /// <see cref="ThemiaQuartzOptions.Authorize"/> gate, the dashboard <see cref="Services"/> bridge
    /// into <c>HttpContext.Items</c>, the embedded content static-file server, and the bridge of a
    /// DI-registered <see cref="IExecutionHistoryStore"/> into the scheduler context.
    /// </summary>
    /// <remarks>
    /// Idempotent: calling this more than once (for example via <see cref="MapThemiaQuartz"/>) registers
    /// the middleware only once. The host owns the Quartz <see cref="IScheduler"/>; it is resolved from
    /// <see cref="ThemiaQuartzOptions.Scheduler"/> or the container's <see cref="IScheduler"/>.
    /// </remarks>
    /// <param name="app">The application builder to register the middleware on.</param>
    /// <returns>The same <paramref name="app"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseThemiaQuartz(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string registeredKey = "Themia.Quartz.MiddlewareRegistered";
        if (app.Properties.ContainsKey(registeredKey))
        {
            return app;
        }

        app.Properties[registeredKey] = true;

        var options = app.ApplicationServices.GetRequiredService<ThemiaQuartzOptions>();

        // Fail fast: the dashboard controllers dereference the scheduler, so a missing one must
        // surface here with a clear message rather than as an opaque NRE on the first request.
        var scheduler = options.Scheduler ?? app.ApplicationServices.GetService<IScheduler>()
            ?? throw new InvalidOperationException(
                "Themia.Quartz: no Quartz IScheduler is available. Set ThemiaQuartzOptions.Scheduler " +
                "or register an IScheduler in DI before calling MapThemiaQuartz/UseThemiaQuartz.");
        options.Scheduler = scheduler;

        // Store bridge: surface a DI-registered execution-history store to the dashboard via the
        // scheduler context.
        var store = app.ApplicationServices.GetService<IExecutionHistoryStore>();
        if (store is not null)
        {
            scheduler.Context.SetExecutionHistoryStore(store);
        }

        var services = Services.Create(options);
        var root = NormalizeRoot(options.VirtualPathRoot);
        var rootPath = "/" + root.TrimEnd('/').TrimStart('/');

        // VirtualPathRoot must be a real sub-path. Empty or "/" collapses rootPath to "/", which would
        // make the authorize gate match every request and the content path become "//Content" — almost
        // always a misconfiguration. Fail fast rather than silently gating the whole application.
        if (rootPath == "/")
        {
            throw new InvalidOperationException(
                "Themia.Quartz: ThemiaQuartzOptions.VirtualPathRoot must be a non-root path segment " +
                "(e.g. \"/jobs\"). An empty or \"/\" value would mount the dashboard at the application root.");
        }

        // DeniedStatusCode must be an HTTP error status. A 2xx/3xx (or 0/negative) would "deny" with a
        // non-error response, silently defeating the gate — fail fast on that misconfiguration.
        if (options.DeniedStatusCode is < 400 or > 599)
        {
            throw new InvalidOperationException(
                "Themia.Quartz: ThemiaQuartzOptions.DeniedStatusCode must be an HTTP error status (400-599). " +
                $"Got {options.DeniedStatusCode}.");
        }

        // Deny-all authorize gate over the dashboard path. null Authorize => always denied.
        // The deny status is configurable (ThemiaQuartzOptions.DeniedStatusCode, default 404).
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                bool allowed;
                if (options.Authorize is null)
                {
                    allowed = false;
                }
                else
                {
                    try
                    {
                        allowed = await options.Authorize(context).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A throwing Authorize predicate is a consumer bug — fail closed (deny), not 500,
                        // consistent with the Themia.Exceptional dashboard. OCE propagates as cancellation.
                        context.RequestServices.GetService<ILoggerFactory>()?
                            .CreateLogger("Themia.Quartz")
                            .LogError(ex, "Themia.Quartz dashboard Authorize predicate threw; denying request.");
                        allowed = false;
                    }
                }

                if (!allowed)
                {
                    await DenyAsync(context, options).ConfigureAwait(false);
                    return;
                }

                // A gated page must not be cacheable: without no-store the browser can re-display the
                // rendered dashboard after the session expires — the back/forward cache serves it from
                // memory without ever contacting the server, so Authorize never runs. no-store also
                // disables bfcache in Chrome/Firefox. Applied to the HTML only (checked once the response
                // has a content type): the CSS/JS/icons under this path aren't sensitive, and no-storing
                // them would re-download semantic.min.css on every navigation for no benefit.
                context.Response.OnStarting(static state =>
                {
                    var response = (HttpResponse)state;
                    if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                        response.Headers.Pragma = "no-cache";
                    }

                    return Task.CompletedTask;
                }, context.Response);
            }

            await next().ConfigureAwait(false);
        });

        // Embedded content static files at {VirtualPathRoot}/Content.
        var contentProvider = new EmbeddedFileProvider(
            typeof(ThemiaQuartzOptions).Assembly,
            "Themia.Quartz.Dashboard.Content");
        app.UseFileServer(new FileServerOptions
        {
            RequestPath = new PathString($"{rootPath}/Content"),
            EnableDefaultFiles = false,
            EnableDirectoryBrowsing = false,
            FileProvider = contentProvider,
        });

        // Bridge the dashboard Services instance into per-request items for the controllers.
        app.Use(async (context, next) =>
        {
            context.Items[typeof(Services)] = services;
            await next().ConfigureAwait(false);
        });

        return app;
    }

    private static string NormalizeRoot(string virtualPathRoot)
    {
        if (string.IsNullOrEmpty(virtualPathRoot))
        {
            return "/";
        }

        return virtualPathRoot.EndsWith('/') ? virtualPathRoot : virtualPathRoot + "/";
    }

    // The single deny path. OnDenied owns the response when set (typically a redirect to the host's login);
    // otherwise, and whenever it throws, the request fails closed with DeniedStatusCode — a broken hook must
    // never be able to serve the dashboard.
    private static async Task DenyAsync(HttpContext context, ThemiaQuartzOptions options)
    {
        if (options.OnDenied is not null)
        {
            try
            {
                await options.OnDenied(context).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("Themia.Quartz")
                    .LogError(ex, "Themia.Quartz dashboard OnDenied hook threw; falling back to the deny status.");
                context.Response.Clear();
            }
        }

        context.Response.StatusCode = options.DeniedStatusCode;
    }
}
