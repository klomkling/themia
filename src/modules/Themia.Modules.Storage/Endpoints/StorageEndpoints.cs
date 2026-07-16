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
    /// <returns>The route group builder for the <em>broker</em> endpoints, for further configuration
    /// (e.g. <c>.RequireAuthorization()</c>). The Local presigned-transfer routes are deliberately NOT in
    /// this group — see <c>transfer</c> below.</returns>
    public static RouteGroupBuilder MapThemiaStorageEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/storage")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The broker endpoints (mint URLs, confirm uploads, delete). Returned to the host, which is
        // expected to gate them with RequireAuthorization().
        var group = endpoints.MapGroup(prefix);

        // The Local presigned-transfer routes live in a SEPARATE group that is never returned, so the
        // host's RequireAuthorization() on the group above cannot reach them. A presigned URL is
        // self-authorizing — the HMAC token IS the credential, exactly as an S3/R2 presigned URL is —
        // and it is handed to a browser (an <img> src, a direct upload) that carries no app session.
        // Gating them behind app auth would 401 a valid signed URL and make Local silently behave
        // differently from S3/R2, whose presigned URLs never touch this app at all.
        var transfer = endpoints.MapGroup(prefix);

        // A PublicBaseUrl that is absolute but does not end with this mount (e.g. https://api.example.com
        // instead of https://api.example.com/storage/public) passes the "is it absolute?" check and then
        // 404s on every single image. Fail at startup instead.
        var localOptions = endpoints.ServiceProvider.GetService<LocalStorageOptions>();
        if (localOptions is not null && !string.IsNullOrWhiteSpace(localOptions.PublicBaseUrl) &&
            !localOptions.PublicBaseUrl.TrimEnd('/').EndsWith($"{prefix}/public", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"LocalStorageOptions.PublicBaseUrl ('{localOptions.PublicBaseUrl}') must end with the public route mount " +
                $"('{prefix}/public') — e.g. https://api.example.com{prefix}/public. Otherwise every public URL 404s.");
        }

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

            var url = await storage.GetUploadUrlAsync(request.Key, request.ContentType, request.SizeBytes, TimeSpan.FromMinutes(15), cancellationToken: ct);
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
        transfer.MapGet("/_local/get", async (
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
        transfer.MapPut("/_local/put", async (
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

        // Serve a public object. No auth, no token: a public object is public by definition. It is mapped
        // in the ungated `transfer` group ON PURPOSE — the group returned to the host is the one adopters
        // gate with RequireAuthorization(), and a public URL that 401s in an <img> tag is a URL that looks
        // right and fails at render time. Local only: with S3/R2 the bytes are served straight from the
        // public bucket's custom domain and never reach this app.
        // SECURITY INVARIANT: this literal-segment route must win over the broker group's catch-all
        // "/{*key}" for a "/public/..." path (ASP.NET routing: a literal segment outranks a catch-all).
        // If that precedence ever changed, public GETs would fall through to the auth-gated broker route
        // and 401. The AuthorizedGroupRouteTests + PublicRouteTests together pin this (public GET returns
        // 200 under RequireAuthorization; broker routes 401).
        transfer.MapGet("/public/{**key}", async (
            string key,
            [FromServices] IStorageProvider provider,
            [FromServices] StorageModuleOptions options,
            HttpResponse response,
            CancellationToken ct) =>
        {
            // Address the public container explicitly. A private object is unreachable through this route
            // no matter what key is supplied, because the prefix — not the caller — selects the container.
            var physicalKey = StorageKey.PublicPrefix + key;

            StorageReadResult? read;
            try
            {
                read = await provider.GetAsync(physicalKey, ct);
            }
            catch (ArgumentException)
            {
                return Results.NotFound(); // traversal / malformed key
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(); // no public container configured on this backend
            }

            if (read is null)
            {
                return Results.NotFound();
            }

            response.Headers.CacheControl = $"public, max-age={(int)options.PublicCacheMaxAge.TotalSeconds}";

            // Defense-in-depth for a same-origin (Local) public route serving user-uploaded bytes: neutralize
            // active content (HTML/SVG script) so a public object with an executable Content-Type cannot run in
            // the app's origin. Harmless to images/video/audio. AllowedContentTypes (an adopter allowlist) is the
            // upload-time control; these headers are the serve-time backstop when it is unset.
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return Results.Stream(read.Content, read.ContentType);
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
