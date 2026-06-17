using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Modules.Storage.Endpoints;

/// <summary>Opt-in minimal-API endpoints for storage. The default flow is presigned direct transfer
/// (the server brokers URLs; the client transfers bytes directly to the backend).</summary>
public static class StorageEndpoints
{
    /// <summary>Maps the storage endpoints onto <paramref name="endpoints"/> under <paramref name="prefix"/>.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix (default <c>/storage</c>).</param>
    /// <returns>The route group builder for further configuration (e.g. <c>.RequireAuthorization()</c>).</returns>
    public static RouteGroupBuilder MapThemiaStorageEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/storage")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);

        // Request a presigned upload URL.
        group.MapPost("/upload-url", async (UploadUrlRequest request, ITenantStorage storage, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ContentType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Key and contentType are required."],
                });
            }

            var url = await storage.GetUploadUrlAsync(request.Key, request.ContentType, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { uploadUrl = url.ToString() });
        });

        // Request a presigned download URL (404 when absent).
        group.MapGet("/{*key}", async (string key, ITenantStorage storage, CancellationToken ct) =>
        {
            if (!await storage.ExistsAsync(key, ct))
            {
                return Results.NotFound();
            }

            var url = await storage.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { downloadUrl = url.ToString() });
        });

        // Delete an object.
        group.MapDelete("/{*key}", async (string key, ITenantStorage storage, CancellationToken ct) =>
        {
            await storage.DeleteAsync(key, ct);
            return Results.NoContent();
        });

        return group;
    }

    /// <summary>The request body for an upload-URL request.</summary>
    /// <param name="Key">The logical key.</param>
    /// <param name="ContentType">The content type the upload will declare.</param>
    public sealed record UploadUrlRequest(string Key, string ContentType);
}
