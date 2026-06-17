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
        // _local URL for the Local backend; resolve both to an absolute URL the client can use. The
        // reservation is pending (invisible) until the client uploads the bytes and POSTs /complete.
        group.MapPost("/upload-url", async (UploadUrlRequest request, HttpRequest httpRequest, ITenantStorage storage, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ContentType) || request.SizeBytes < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Key and contentType are required, and sizeBytes must be non-negative."],
                });
            }

            var url = await storage.GetUploadUrlAsync(request.Key, request.ContentType, request.SizeBytes, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { uploadUrl = ToAbsolute(httpRequest, prefix, url) });
        });

        // Confirm a presigned upload after the client has transferred the bytes: reconciles quota to the
        // actual stored size and makes the object visible (or 409 via StorageQuotaExceededException).
        group.MapPost("/complete", async (CompleteRequest request, ITenantStorage storage, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Key is required."],
                });
            }

            var stored = await storage.CompleteUploadAsync(request.Key, ct);
            return Results.Ok(new { key = stored.Key, sizeBytes = stored.SizeBytes });
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
        // Enforces the configured size cap before writing (413 when exceeded) to bound memory/DoS.
        group.MapPut("/_local/put", async (
            [FromQuery] string key,
            [FromQuery] string token,
            HttpRequest request,
            [FromServices] LocalUrlSigner? signer,
            [FromServices] IStorageProvider provider,
            [FromServices] StorageModuleOptions options,
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

            // Buffer with a hard cap so the size limit holds even for a non-seekable request body;
            // reject (without writing) once the total exceeds MaxObjectSizeBytes.
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > options.MaxObjectSizeBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            await provider.PutAsync(key, buffer, new StoragePutOptions(request.ContentType ?? "application/octet-stream"), ct);
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

    /// <summary>The request body for an upload-URL request. After uploading the bytes to the returned URL,
    /// the client must POST <c>/complete</c> with the same key to confirm the upload and make it visible.</summary>
    /// <param name="Key">The logical key.</param>
    /// <param name="ContentType">The content type the upload will declare.</param>
    /// <param name="SizeBytes">The declared object size in bytes (reserved against quota up front).</param>
    public sealed record UploadUrlRequest(string Key, string ContentType, long SizeBytes);

    /// <summary>The request body for confirming a presigned upload (after the client has uploaded the bytes).</summary>
    /// <param name="Key">The logical key whose presigned upload to confirm.</param>
    public sealed record CompleteRequest(string Key);
}
