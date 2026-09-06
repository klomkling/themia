using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Mounts the self-rendered, read-only exceptions dashboard.</summary>
public static class ExceptionalDashboardEndpoints
{
    /// <summary>Maps the exceptions dashboard (list at <paramref name="path"/>, detail at
    /// <c>{path}/{guid}</c>) and returns the route group. Access is governed by
    /// <see cref="ExceptionalDashboardOptions.Authorize"/> (fail-closed when unset).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">Route prefix (default <c>/exceptions</c>).</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The route group for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaExceptional(
        this IEndpointRouteBuilder endpoints,
        string path = "/exceptions",
        Action<ExceptionalDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new ExceptionalDashboardOptions();
        configure?.Invoke(options);

        // Fail fast on misconfiguration so a config typo surfaces at startup, not as silently clamped paging.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DefaultPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPageSize, options.DefaultPageSize);

        if (options.Authorize is null)
        {
            var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Themia.Exceptional.AspNetCore");
            logger?.LogWarning(
                "Exceptions dashboard mounted at {Path} without an Authorize predicate; all requests are denied.", path);
        }

        var group = endpoints.MapGroup(path);
        group.MapGet("", (HttpContext ctx, IExceptionStore store, CancellationToken ct) => HandleListAsync(ctx, store, options, path, ct));
        group.MapGet("{guid:guid}", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) => HandleDetailAsync(ctx, store, options, path, guid, ct));
        group.MapGet("dashboard.css", (HttpContext ctx) => HandleCssAsync(ctx, options));

        if (options.EnableActions)
        {
            group.MapPost("{guid:guid}/protect", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.ProtectAsync, ct));
            group.MapPost("{guid:guid}/delete", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.DeleteAsync, ct));
            group.MapPost("{guid:guid}/hard-delete", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.HardDeleteAsync, ct));
        }

        return group;
    }

    private static async Task HandleListAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { await DenyAsync(ctx, options).ConfigureAwait(false); return; }

        var filter = BuildFilter(ctx.Request.Query, options);
        var result = await store.ListAsync(filter, ct).ConfigureAwait(false);
        var chrome = new DashboardChrome(options.Title, path, options.CustomStyleSheet, options.CustomFavicon, options.HeadHtml, options.BodyStartHtml, options.Heading);
        // The list page renders no POST forms, so it needs no CSRF token (and must not set the cookie).
        await WriteHtmlAsync(ctx, DashboardHtml.List(chrome, result.Items, result.Total, filter, DateTime.UtcNow), ct).ConfigureAwait(false);
    }

    private static async Task HandleDetailAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, Guid guid, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { await DenyAsync(ctx, options).ConfigureAwait(false); return; }

        var entry = await store.GetAsync(guid, ct).ConfigureAwait(false);
        if (entry is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var token = options.EnableActions ? IssueCsrf(ctx) : null;
        var chrome = new DashboardChrome(options.Title, path, options.CustomStyleSheet, options.CustomFavicon, options.HeadHtml, options.BodyStartHtml, options.Heading);
        await WriteHtmlAsync(ctx, DashboardHtml.Detail(chrome, entry, options.ShowRequestBody, options.ShowRequestContext, token), ct).ConfigureAwait(false);
    }

    private const string CsrfCookie = "__themia_csrf";

    private static async Task HandleActionAsync(
        HttpContext ctx, ExceptionalDashboardOptions options, string path, Guid guid,
        Func<Guid, CancellationToken, Task<bool>> action, CancellationToken ct)
    {
        // Auth gate runs FIRST: a denied request must 404 regardless of any token presented.
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { await DenyAsync(ctx, options).ConfigureAwait(false); return; }
        if (!await ValidCsrfAsync(ctx, ct).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

        await action(guid, ct).ConfigureAwait(false);
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = path; // back to the list
    }

    private static async Task<bool> ValidCsrfAsync(HttpContext ctx, CancellationToken ct)
    {
        var cookie = ctx.Request.Cookies[CsrfCookie];
        // ReadFormAsync (not the sync Request.Form) — Kestrel disallows synchronous body reads by default.
        var form = ctx.Request.HasFormContentType ? (await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false))["__token"].ToString() : null;
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(form) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(form)))
        {
            return false;
        }

        // Same-origin: Origin (preferred) or Referer host must equal the request host.
        var host = ctx.Request.Host.Value;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase);
        }

        var referer = ctx.Request.Headers.Referer.ToString();
        return !string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r) && string.Equals(r.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    // Issues (and persists) the per-session CSRF token, returning it for embedding in rendered forms.
    private static string IssueCsrf(HttpContext ctx)
    {
        var existing = ctx.Request.Cookies[CsrfCookie];
        if (!string.IsNullOrEmpty(existing)) return existing;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ctx.Response.Cookies.Append(CsrfCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            Path = "/",
        });
        return token;
    }

    // The stylesheet is gated exactly like the pages. Serving it unauthenticated returns 200 for a path
    // whose siblings return 404, which confirms the dashboard's mount point to anyone who asks — the very
    // thing OnDenied's own remarks call out the route-hiding 404 as protecting.
    //
    // A denial here is a bare 404 and never runs OnDenied: that hook typically redirects to the host's
    // login page, and a redirect on a subresource makes the browser fetch HTML where it asked for CSS.
    // OnDenied is for navigations.
    private static async Task HandleCssAsync(HttpContext ctx, ExceptionalDashboardOptions options)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false))
        {
            PreventCaching(ctx.Response);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        PreventCaching(ctx.Response);
        ctx.Response.ContentType = "text/css; charset=utf-8";
        await ctx.Response.WriteAsync(DashboardCss.Content).ConfigureAwait(false);
    }

    // The single deny path. OnDenied owns the response when set (typically a redirect to the host's login);
    // otherwise, and whenever it throws, the request fails closed with the route-hiding 404 — a broken hook
    // must never be able to serve the dashboard.
    private static async Task DenyAsync(HttpContext ctx, ExceptionalDashboardOptions options)
    {
        // Set before OnDenied runs so the hook can override the headers when it owns the response, and so
        // the fallback 404 below is never cacheable.
        PreventCaching(ctx.Response);

        if (options.OnDenied is not null)
        {
            try
            {
                await options.OnDenied(ctx).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("Themia.Exceptional.AspNetCore")
                    .LogError(ex, "Exceptions dashboard OnDenied hook threw; falling back to the deny status.");
                ctx.Response.Clear();

                // Response.Clear() drops headers along with the body, taking the PreventCaching call above
                // with it. Without this the fallback 404 is cacheable again.
                PreventCaching(ctx.Response);
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // A gated response must not be cacheable, for two separate reasons.
    //
    // Private cache: without no-store the browser can re-display the rendered dashboard after the session
    // expires — the back/forward cache serves it from memory without ever contacting the server, so
    // Authorize never runs. no-store also disables bfcache in Chrome/Firefox.
    //
    // Shared cache: a proxy or CDN that stored an authorized 200 can serve it to an unauthenticated caller,
    // which defeats Authorize without the predicate running at all. Gating a route at the origin while
    // leaving its response cacheable only moves the disclosure one layer out. Vary states what the response
    // actually depends on, for caches that honour it but not no-store.
    private static void PreventCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Vary = "Cookie, Authorization";
    }

    private static async Task<bool> AuthorizedAsync(HttpContext ctx, ExceptionalDashboardOptions options)
    {
        if (options.Authorize is null)
        {
            return false;
        }

        try
        {
            return await options.Authorize(ctx).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throwing Authorize predicate (e.g. a flaky identity lookup) must fail closed — deny and
            // hide (404), never 500 or serve data. Logged at the boundary, not swallowed silently.
            // OperationCanceledException is excluded: a client abort is cancellation flow, not a denial,
            // and must propagate (the host treats it as cancellation, not a server error).
            ctx.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger("Themia.Exceptional.AspNetCore")
                .LogError(ex, "Exceptions dashboard Authorize predicate threw; denying request.");
            return false;
        }
    }

    private static ExceptionFilter BuildFilter(IQueryCollection query, ExceptionalDashboardOptions options)
    {
        var filter = new ExceptionFilter
        {
            Page = ParseInt(query["page"], 1, 1, int.MaxValue),
            PageSize = ParseInt(query["pageSize"], options.DefaultPageSize, 1, options.MaxPageSize),
            Search = NullIfEmpty(query["q"]),
            ApplicationName = NullIfEmpty(query["app"]),
            TenantId = NullIfEmpty(query["tenant"]),
            IncludeDeleted = string.Equals(query["includeDeleted"], "true", StringComparison.OrdinalIgnoreCase),
        };
        if (DateTime.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var from)) filter.From = from;
        if (DateTime.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var to)) filter.To = to;
        return filter;
    }

    private static int ParseInt(StringValues raw, int fallback, int min, int max)
    {
        var value = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        // Math.Max guards against a misconfigured MaxPageSize < min (would make Clamp throw).
        return Math.Clamp(value, min, Math.Max(min, max));
    }

    private static string? NullIfEmpty(StringValues raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.ToString();

    private static Task WriteHtmlAsync(HttpContext ctx, string html, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        PreventCaching(ctx.Response);
        return ctx.Response.WriteAsync(html, Encoding.UTF8, ct);
    }
}
