using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Themia.Content.AspNetCore;

/// <summary>Maps Themia.Content's HTTP endpoints. A consumer that keeps its own controllers maps neither.</summary>
public static class ContentEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string LoggerCategory = "Themia.Content.AspNetCore";

    /// <summary>Maps <c>GET {prefix}/{slug}?lang=</c>: the published page in the requested language, else the fallback
    /// language, else 404. No authorization. Caching is the consumer's decision.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix, including any API version segment (for example <c>/api/v1/pages</c>).</param>
    /// <returns>The route group, for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaContentPublicEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/pages")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("{slug}", async (string slug, string? lang, IContentPageService service, CancellationToken ct) =>
        {
            var page = await service.GetPublishedAsync(slug, lang, ct).ConfigureAwait(false);
            return page is null ? ContentHttpResults.NotFound() : ContentHttpResults.Data(page);
        });
        return group;
    }

    /// <summary>
    /// Maps the admin routes under <paramref name="prefix"/>: <c>GET pages</c>, <c>GET pages/{slug}/{language}</c>,
    /// <c>PUT pages/{slug}/{language}</c>, <c>GET pages/{slug}/{language}/revisions</c> and
    /// <c>POST pages/{slug}/{language}/revert</c>.
    /// </summary>
    /// <remarks>Every route runs <see cref="ContentAdminOptions.Authorize"/> first and is refused — 401 when the request is
    /// unauthenticated, 403 otherwise — when it is unset, returns <see langword="false"/> or throws.</remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="options">Authorization and editor resolution.</param>
    /// <param name="prefix">The route prefix, including any API version segment.</param>
    /// <returns>The route group, for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaContentAdminEndpoints(
        this IEndpointRouteBuilder endpoints, ContentAdminOptions options, string prefix = "/admin/content")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
        if (options.Authorize is null)
        {
            logger?.LogWarning(
                "Content admin endpoints mounted at {Prefix} without an Authorize delegate; every request is refused.", prefix);
        }

        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter((context, next) => AuthorizeAsync(context, next, options, logger));

        group.MapGet("pages", async (int? page, int? limit, IContentPageService service, CancellationToken ct) =>
            await PagedAsync(page, limit, (p, l) => service.ListAsync(p, l, ct)).ConfigureAwait(false));

        group.MapGet("pages/{slug}/{language}", async (string slug, string language, IContentPageService service, CancellationToken ct) =>
        {
            var found = await service.GetForEditAsync(slug, language, ct).ConfigureAwait(false);
            return found is null ? ContentHttpResults.NotFound() : ContentHttpResults.Data(found);
        });

        group.MapPut("pages/{slug}/{language}", async (
            string slug, string language, SavePageRequest body, HttpContext http, IContentPageService service, CancellationToken ct) =>
            ContentHttpResults.FromSave(await service.SaveAsync(
                new ContentPageSave(slug, language, body.Title, body.Markdown, body.IsPublished, body.ExpectedVersion,
                    body.ChangeSummary, options.ResolveEditorId(http)),
                ct).ConfigureAwait(false)));

        group.MapGet("pages/{slug}/{language}/revisions", async (
            string slug, string language, int? page, int? limit, IContentPageService service, CancellationToken ct) =>
            await PagedAsync(page, limit, (p, l) => service.GetRevisionsAsync(slug, language, p, l, ct)).ConfigureAwait(false));

        group.MapPost("pages/{slug}/{language}/revert", async (
            string slug, string language, RevertPageRequest body, HttpContext http, IContentPageService service, CancellationToken ct) =>
            ContentHttpResults.FromSave(await service.RevertAsync(
                new ContentPageRevert(slug, language, body.Version, body.ExpectedVersion, body.ChangeSummary, options.ResolveEditorId(http)),
                ct).ConfigureAwait(false)));

        return group;
    }

    private static async ValueTask<object?> AuthorizeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, ContentAdminOptions options, ILogger? logger)
    {
        var http = context.HttpContext;
        bool allowed;
        try
        {
            allowed = options.Authorize is not null && await options.Authorize(http).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken delegate must not open the routes. Logged once here; the request is refused below.
            logger?.LogWarning(exception, "Content admin Authorize delegate threw; the request is refused.");
            allowed = false;
        }

        if (allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        return http.User.Identity?.IsAuthenticated == true ? ContentHttpResults.Forbidden() : ContentHttpResults.Unauthorized();
    }

    private static async Task<IResult> PagedAsync<T>(int? page, int? limit, Func<int, int, Task<PagedResult<T>>> query)
    {
        var p = page ?? 1;
        var l = limit ?? DefaultPageSize;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (p < 1 || p > int.MaxValue / MaxPageSize)
        {
            errors["page"] = ["Page must be 1 or greater."];
        }

        if (l < 1 || l > MaxPageSize)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return ContentHttpResults.Invalid(errors);
        }

        return ContentHttpResults.List(await query(p, l).ConfigureAwait(false), p, l);
    }
}
