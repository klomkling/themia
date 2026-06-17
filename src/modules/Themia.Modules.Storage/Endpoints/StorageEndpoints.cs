using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Themia.Storage;
using Themia.Storage.Local;

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

        // Request a presigned upload URL. The provider returns an absolute URL for S3/R2 and a relative
        // _local URL for the Local backend; resolve both to an absolute URL the client can use.
        group.MapPost("/upload-url", async (UploadUrlRequest request, HttpRequest httpRequest, ITenantStorage storage, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ContentType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Key and contentType are required."],
                });
            }

            var url = await storage.GetUploadUrlAsync(request.Key, request.ContentType, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { uploadUrl = ToAbsolute(httpRequest, prefix, url) });
        });

        // Serve a Local presigned download (the token authorizes exactly this physical key).
        // Returns 404 when the backend is not Local (the signer is only registered for UseLocal).
        group.MapGet("/_local/get", async (
            [FromQuery] string key,
            [FromQuery] string token,
            [FromServices] LocalUrlSigner? signer,
            [FromServices] IStorageProvider provider,
            CancellationToken ct) =>
        {
            if (signer is null)
            {
                return Results.NotFound();
            }

            if (!signer.TryVerify(key, PresignedUrlOperation.Get, token, DateTimeOffset.UtcNow))
            {
                return Results.Unauthorized();
            }

            var read = await provider.GetAsync(key, ct);
            return read is null ? Results.NotFound() : Results.Stream(read.Content, read.ContentType);
        });

        // Accept a Local presigned upload (the token authorizes exactly this physical key).
        // Returns 404 when the backend is not Local (the signer is only registered for UseLocal).
        group.MapPut("/_local/put", async (
            [FromQuery] string key,
            [FromQuery] string token,
            HttpRequest request,
            [FromServices] LocalUrlSigner? signer,
            [FromServices] IStorageProvider provider,
            CancellationToken ct) =>
        {
            if (signer is null)
            {
                return Results.NotFound();
            }

            if (!signer.TryVerify(key, PresignedUrlOperation.Put, token, DateTimeOffset.UtcNow))
            {
                return Results.Unauthorized();
            }

            await provider.PutAsync(key, request.Body, new StoragePutOptions(request.ContentType ?? "application/octet-stream"), ct);
            return Results.NoContent();
        });

        // Request a presigned download URL (404 when absent).
        group.MapGet("/{*key}", async (string key, HttpRequest httpRequest, ITenantStorage storage, CancellationToken ct) =>
        {
            if (!await storage.ExistsAsync(key, ct))
            {
                return Results.NotFound();
            }

            var url = await storage.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { downloadUrl = ToAbsolute(httpRequest, prefix, url) });
        });

        // Delete an object.
        group.MapDelete("/{*key}", async (string key, ITenantStorage storage, CancellationToken ct) =>
        {
            await storage.DeleteAsync(key, ct);
            return Results.NoContent();
        });

        return group;
    }

    // Resolves a provider URL to an absolute string: absolute URLs (S3/R2) pass through; relative
    // Local URLs are rebased onto this request's scheme/host/path-base + group prefix.
    private static string ToAbsolute(HttpRequest httpRequest, string prefix, Uri url) =>
        url.IsAbsoluteUri
            ? url.ToString()
            : new Uri(new Uri($"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.PathBase}{prefix}/"), url).ToString();

    /// <summary>The request body for an upload-URL request.</summary>
    /// <param name="Key">The logical key.</param>
    /// <param name="ContentType">The content type the upload will declare.</param>
    public sealed record UploadUrlRequest(string Key, string ContentType);
}
