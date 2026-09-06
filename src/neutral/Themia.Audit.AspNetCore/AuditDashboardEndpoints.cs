using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Themia.Audit.AspNetCore;

/// <summary>Mounts the self-rendered, read-only audit dashboard. List and detail only — no mutation
/// endpoint of any kind (design §13: an audit log an operator can edit is not an audit log).</summary>
public static class AuditDashboardEndpoints
{
    private const string LoggerCategory = "Themia.Audit.AspNetCore";

    /// <summary>Maps the audit dashboard (list at <paramref name="path"/>, detail at
    /// <c>{path}/{eventUid}</c>) and returns the route group. Access is governed by
    /// <see cref="AuditDashboardOptions.Authorize"/> (fail-closed when unset). The detail route is keyed
    /// on <see cref="AuditEntry.EventUid"/>, never the internal <c>bigint</c> row id — a sequential
    /// integer in a URL invites walking the table by hand, and the <c>guid</c> route constraint means a
    /// path like <c>{path}/1</c> never resolves. The dashboard opens its own connection per request via
    /// <see cref="IAuditDialect.CreateConnection"/> (it has no unit of work of its own, and must never
    /// hold a lock on the audit table while an adopter's business transaction is open — design §13).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">Route prefix (default <c>/audit</c>).</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The route group for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaAuditDashboard(
        this IEndpointRouteBuilder endpoints,
        string path = "/audit",
        Action<AuditDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new AuditDashboardOptions();
        configure?.Invoke(options);

        // Fail fast on misconfiguration so a config typo surfaces at startup, not as silently clamped paging.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DefaultPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPageSize, options.DefaultPageSize);

        if (options.Authorize is null)
        {
            var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
            logger?.LogWarning(
                "Audit dashboard mounted at {Path} without an Authorize predicate; all requests are denied.", path);
        }

        var group = endpoints.MapGroup(path);
        group.MapGet("", (HttpContext ctx, IAuditStore store, IAuditDialect dialect, IOptions<AuditOptions> auditOptions, CancellationToken ct) =>
            HandleListAsync(ctx, store, dialect, auditOptions.Value, options, path, ct));
        group.MapGet("{eventUid:guid}", (Guid eventUid, HttpContext ctx, IAuditStore store, IAuditDialect dialect, IOptions<AuditOptions> auditOptions, CancellationToken ct) =>
            HandleDetailAsync(ctx, store, dialect, auditOptions.Value, options, path, eventUid, ct));
        group.MapGet("dashboard.css", (HttpContext ctx) => HandleCssAsync(ctx, options));

        return group;
    }

    private static async Task HandleListAsync(
        HttpContext ctx, IAuditStore store, IAuditDialect dialect, AuditOptions auditOptions,
        AuditDashboardOptions options, string path, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { await DenyAsync(ctx, options).ConfigureAwait(false); return; }

        var query = BuildQuery(ctx.Request.Query, options);
        await using var connection = dialect.CreateConnection(auditOptions.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var result = await store.QueryAsync(query, connection, ct).ConfigureAwait(false);

        var chrome = new DashboardChrome(options.Title, path, options.CustomStyleSheet, options.CustomFavicon, options.HeadHtml, options.BodyStartHtml, options.Heading);
        await WriteHtmlAsync(ctx, DashboardHtml.List(chrome, result.Items, result.Total, query, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    private static async Task HandleDetailAsync(
        HttpContext ctx, IAuditStore store, IAuditDialect dialect, AuditOptions auditOptions,
        AuditDashboardOptions options, string path, Guid eventUid, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { await DenyAsync(ctx, options).ConfigureAwait(false); return; }

        await using var connection = dialect.CreateConnection(auditOptions.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var entry = await store.GetAsync(eventUid, connection, ct).ConfigureAwait(false);
        if (entry is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var chrome = new DashboardChrome(options.Title, path, options.CustomStyleSheet, options.CustomFavicon, options.HeadHtml, options.BodyStartHtml, options.Heading);
        await WriteHtmlAsync(ctx, DashboardHtml.Detail(chrome, entry, options.ShowData), ct).ConfigureAwait(false);
    }

    // Gated like the list/detail routes: an unauthenticated 200 here would confirm the mount path even
    // though no audit data leaks (the 404 on the other two routes exists precisely to conceal that a
    // dashboard is mounted at all). OnDenied is deliberately NOT invoked on denial — it typically redirects
    // to the host's login page, and redirecting a stylesheet request means the browser fetches HTML where
    // it expected CSS. OnDenied is for navigations; a subresource just 404s.
    private static async Task HandleCssAsync(HttpContext ctx, AuditDashboardOptions options)
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
    private static async Task DenyAsync(HttpContext ctx, AuditDashboardOptions options)
    {
        // Set before OnDenied runs, not after, so a hook that owns the response (e.g. a login redirect)
        // is free to override these headers with its own caching policy if it needs to.
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
                    .CreateLogger(LoggerCategory)
                    .LogError(ex, "Audit dashboard OnDenied hook threw; falling back to the deny status.");
                // Response.Clear() wipes headers along with the body/status, including the PreventCaching
                // call above — reapply it so a broken hook can't leave the fallback 404 cacheable either.
                ctx.Response.Clear();
                PreventCaching(ctx.Response);
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // A gated response must not be cacheable, for two distinct reasons. First, the browser's own
    // back/forward cache: without no-store, a browser can re-display the rendered dashboard after the
    // session expires, served from memory without ever contacting the server, so Authorize never runs
    // (no-store also disables bfcache in Chrome/Firefox). Second, and separately, a SHARED cache (a
    // corporate proxy, a CDN, any intermediary): the response genuinely varies by who is asking and says
    // nothing about it, so such a cache is entitled to reuse one response for every caller — an
    // authorized 200 served back to an unauthenticated prober defeats Authorize without the predicate
    // ever running, and the inverse (an unauthenticated 404 served back to an authorized admin) breaks
    // the dashboard for them. This applies to every response this dashboard writes, not just the HTML
    // pages — a future reader must not "optimize" the stylesheet route back to a long max-age.
    private static void PreventCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Vary = "Cookie, Authorization";
    }

    private static async Task<bool> AuthorizedAsync(HttpContext ctx, AuditDashboardOptions options)
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
                .CreateLogger(LoggerCategory)
                .LogError(ex, "Audit dashboard Authorize predicate threw; denying request.");
            return false;
        }
    }

    private static AuditQuery BuildQuery(IQueryCollection query, AuditDashboardOptions options)
    {
        var built = new AuditQuery
        {
            TenantId = NullIfEmpty(query["tenant"]),
            HostLevelOnly = string.Equals(query["hostOnly"], "true", StringComparison.OrdinalIgnoreCase),
            ActorId = NullIfEmpty(query["actor"]),
            EntityType = NullIfEmpty(query["entityType"]),
            EntityId = NullIfEmpty(query["entityId"]),
            Category = ParseEnum<AuditCategory>(query["category"]),
            Outcome = ParseEnum<AuditOutcome>(query["outcome"]),
            Page = ParseInt(query["page"], 1, 1, int.MaxValue),
            PageSize = ParseInt(query["pageSize"], options.DefaultPageSize, 1, options.MaxPageSize),
        };

        if (DateTimeOffset.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) built = built with { From = from };
        if (DateTimeOffset.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) built = built with { To = to };
        return built;
    }

    private static TEnum? ParseEnum<TEnum>(StringValues raw)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

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
