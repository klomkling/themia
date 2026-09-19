using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.Urls;

namespace Themia.Storage.AspNetCore;

/// <summary>Serves the Local provider's presigned downloads.</summary>
public static class LocalStorageEndpoints
{
    /// <summary>The headers every response from the download route carries, success or refusal.</summary>
    /// <remarks>
    /// <c>nosniff</c> alone is not enough. It stops a browser <i>guessing</i> a type; it does nothing for a file
    /// whose declared type is already dangerous. An uploaded SVG with a <c>&lt;script&gt;</c>, stored as
    /// <c>image/svg+xml</c>, opened directly from its presigned link, runs on this API's origin — the same for
    /// <c>text/html</c>. <c>Content-Security-Policy: sandbox</c> gives the response a unique origin and no
    /// script execution, while images and PDFs still render. <c>private, no-store</c> because a presigned
    /// response is one user's object and must not land in a shared cache.
    /// </remarks>
    internal static readonly (string Name, string Value)[] SecurityHeaders =
    [
        ("Cache-Control", "private, no-store"),
        ("X-Content-Type-Options", "nosniff"),
        ("Content-Security-Policy", "sandbox"),
    ];

    /// <summary>
    /// Maps <c>GET {prefix}/_local/get?key=…&amp;token=…</c>, which verifies the token a
    /// <see cref="LocalStorageProvider"/> signed and streams the object.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">
    /// The mount, e.g. <c>/api/v1/storage</c>. Must match what <see cref="StorageUrlOptions.PresignedBaseUrl"/>
    /// ends with, when that is set.
    /// </param>
    /// <returns><paramref name="endpoints"/> — deliberately not the route, see remarks.</returns>
    /// <remarks>
    /// <b>Anonymous by construction.</b> The route is mapped in its own group and not handed back, so a host's
    /// <c>RequireAuthorization()</c> cannot reach it, and it carries <c>AllowAnonymous</c> so an authorization
    /// <c>FallbackPolicy</c> cannot either. The token is the credential, as an S3 presigned URL's signature
    /// is, and the link is opened by a browser or mail client that carries no app session; gating it would
    /// reject every valid link.
    /// <para>
    /// A missing, invalid or expired token is a bare <c>403</c>; a missing object, <c>404</c>.
    /// </para>
    /// <para>
    /// <b>The token is a bearer credential for the life of the URL</b>, and it travels in the query string. If
    /// your request logging writes query strings, exclude this route.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// At startup: no <see cref="LocalUrlSigner"/> is registered — every request would otherwise fail in
    /// production — or <see cref="StorageUrlOptions.PresignedBaseUrl"/> is set and does not end with
    /// <paramref name="prefix"/>, so every minted link would 404.
    /// </exception>
    public static IEndpointRouteBuilder MapThemiaLocalStorage(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var mount = "/" + prefix.Trim('/');

        if (endpoints.ServiceProvider.GetService<LocalUrlSigner>() is null)
        {
            throw new InvalidOperationException(
                "MapThemiaLocalStorage needs a LocalUrlSigner registered, built from the same SigningKey as the " +
                "LocalStorageProvider: services.AddSingleton(new LocalUrlSigner(signingKey)). Without it every " +
                "download would fail.");
        }

        var baseUrl = endpoints.ServiceProvider.GetService<IOptions<StorageUrlOptions>>()?.Value.PresignedBaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl) &&
            !new Uri(baseUrl, UriKind.Absolute).AbsolutePath.TrimEnd('/').EndsWith(mount, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"StorageUrlOptions.PresignedBaseUrl ('{baseUrl}') must end with the mount passed to " +
                $"MapThemiaLocalStorage ('{mount}'), e.g. https://api.example.com{mount}. Otherwise every link 404s.");
        }

        // Its own group, never returned, so a host's RequireAuthorization() on its own groups cannot reach it —
        // and AllowAnonymous() explicitly, because not returning the group is not enough: an authorization
        // FallbackPolicy (a common hardening) applies to every endpoint WITHOUT auth metadata, and would put
        // this route behind a login that no presigned link can satisfy.
        endpoints.MapGroup(mount).MapGet("/_local/get", ServeAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> ServeAsync(HttpContext context, string? key, string? token)
    {
        foreach (var (name, value) in SecurityHeaders)
        {
            context.Response.Headers[name] = value;
        }

        var services = context.RequestServices;
        var signer = services.GetRequiredService<LocalUrlSigner>();
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;

        // One answer for every rejection, so a prober learns nothing about which part was wrong.
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token) ||
            !signer.TryVerify(key, PresignedUrlOperation.Get, token, clock.GetUtcNow()))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var read = await services.GetRequiredService<IStorageProvider>()
            .GetAsync(key, context.RequestAborted).ConfigureAwait(false);
        return read is null ? Results.NotFound() : Results.Stream(read.Content, read.ContentType);
    }
}
