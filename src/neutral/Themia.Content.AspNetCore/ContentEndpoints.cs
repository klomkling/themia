using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Themia.Content.AspNetCore;

/// <summary>Maps Themia.Content's HTTP endpoints. A consumer that keeps its own controllers maps neither.</summary>
public static class ContentEndpoints
{
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
}
