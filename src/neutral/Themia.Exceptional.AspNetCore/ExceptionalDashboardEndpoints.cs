using System.Globalization;
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
        return group;
    }

    private static async Task HandleListAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var filter = BuildFilter(ctx.Request.Query, options);
        var result = await store.ListAsync(filter, ct).ConfigureAwait(false);
        await WriteHtmlAsync(ctx, DashboardHtml.List(options.Title, path, result.Items, result.Total, filter), ct).ConfigureAwait(false);
    }

    private static async Task HandleDetailAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, Guid guid, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var entry = await store.GetAsync(guid, ct).ConfigureAwait(false);
        if (entry is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        await WriteHtmlAsync(ctx, DashboardHtml.Detail(options.Title, path, entry, options.ShowRequestBody), ct).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            // A throwing Authorize predicate (e.g. a flaky identity lookup) must fail closed — deny and
            // hide (404), never 500 or serve data. Logged at the boundary, not swallowed silently.
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
        return ctx.Response.WriteAsync(html, Encoding.UTF8, ct);
    }
}
