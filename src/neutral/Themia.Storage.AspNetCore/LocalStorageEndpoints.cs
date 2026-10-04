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

    /// <summary>The headers the public route sends. The same pair the Storage module's public route sends, and no
    /// <c>Cache-Control</c>: unlike a presigned download, a public object is meant to be cached.</summary>
    internal static readonly (string Name, string Value)[] PublicSecurityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Content-Security-Policy", "sandbox; default-src 'none'"),
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

        // Compared without trailing slashes on either side — so a root mount ("/") is the empty path and
        // matches any base — and ignoring case, because routing does: a base of https://host/API/v1/storage
        // serves links from a /api/v1/storage mount perfectly well and must not be refused at startup.
        var baseUrl = endpoints.ServiceProvider.GetService<IOptions<StorageUrlOptions>>()?.Value.PresignedBaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl) &&
            !new Uri(baseUrl, UriKind.Absolute).AbsolutePath.TrimEnd('/')
                .EndsWith(mount.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Maps <c>GET {mount}/{key}</c>, which streams an object from the Local provider's public container — the
    /// route <see cref="LocalStorageProvider.GetPublicUrl"/> links point at.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="provider">
    /// The provider that owns the public container. Pass the <see cref="LocalStorageProvider"/> itself, not the
    /// app's <see cref="IStorageProvider"/>: behind a split that one is not the Local provider.
    /// </param>
    /// <param name="mount">
    /// Where to mount, e.g. <c>/media</c>. Defaults to the path of <see cref="LocalStorageProvider.PublicBaseUrl"/>.
    /// Pass it when the request path differs from the link's path (<c>UsePathBase</c>, a proxy that strips a
    /// prefix) or when the base url has no path at all (a CDN host).
    /// </param>
    /// <returns><paramref name="endpoints"/> — deliberately not the route, see remarks.</returns>
    /// <remarks>
    /// Anonymous by construction, as <see cref="MapThemiaLocalStorage"/> is: a public object has no credential.
    /// Only the public container is reachable — the key is always read under the public prefix, and the provider
    /// refuses a public root that equals its private root, so a private object cannot be named through this route.
    /// Every response carries <c>nosniff</c> and <c>Content-Security-Policy: sandbox; default-src 'none'</c> (the
    /// policy the Storage module's public route sends), so an uploaded SVG or HTML file cannot run script on this
    /// origin. <c>Cache-Control</c> and any other header are the host's to add: public objects are meant to be cached.
    /// <para>
    /// Do not also map the Storage module's <c>/public/{**key}</c> route at the same path: both would match.
    /// </para>
    /// <para>
    /// <b>Not a static-file server.</b> GET only: no <c>HEAD</c>, no <c>Range</c> (so no seeking in audio or
    /// video), no <c>ETag</c> or <c>Last-Modified</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// At startup: <paramref name="provider"/> has no public container; the mount is empty (a base url with no
    /// path and no <paramref name="mount"/> — it would answer every anonymous GET on the site); or the mount
    /// contains a <c>{</c> or <c>}</c>, which routing would read as a template.
    /// </exception>
    public static IEndpointRouteBuilder MapThemiaLocalPublicStorage(
        this IEndpointRouteBuilder endpoints, LocalStorageProvider provider, string? mount = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.PublicBaseUrl is null)
        {
            throw new InvalidOperationException(
                "MapThemiaLocalPublicStorage needs a provider with a public container: set " +
                "LocalStorageOptions.PublicRootPath and PublicBaseUrl.");
        }

        // The provider guarantees an absolute http(s) base url. AbsolutePath is percent-encoded and a route template
        // is matched against the decoded request path, so it is decoded here.
        var path = mount ?? Uri.UnescapeDataString(new Uri(provider.PublicBaseUrl).AbsolutePath);
        var route = "/" + path.Trim('/');
        if (route == "/")
        {
            throw new InvalidOperationException(
                $"MapThemiaLocalPublicStorage has no mount: PublicBaseUrl ('{provider.PublicBaseUrl}') has no path. " +
                "Pass a mount, e.g. MapThemiaLocalPublicStorage(provider, \"/media\"); mounting at the root would " +
                "answer every anonymous GET on the site.");
        }

        if (route.AsSpan().IndexOfAny('{', '}') >= 0)
        {
            throw new InvalidOperationException(
                $"The mount '{route}' contains '{{' or '}}', which routing reads as a template, not a path.");
        }

        endpoints.MapGroup(route)
            .MapGet("/{**key}", (HttpContext context, string? key) => ServePublicAsync(provider, context, key))
            .AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> ServePublicAsync(LocalStorageProvider provider, HttpContext context, string? key)
    {
        foreach (var (name, value) in PublicSecurityHeaders)
        {
            context.Response.Headers[name] = value;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return Results.NotFound();
        }

        StorageReadResult? read;
        try
        {
            read = await provider.GetAsync(StorageKey.PublicPrefix + key, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // A key the provider refuses (traversal, absolute) names nothing: the same answer as a missing object.
            return Results.NotFound();
        }

        return read is null ? Results.NotFound() : Results.Stream(read.Content, read.ContentType);
    }

    private static async Task<IResult> ServeAsync(HttpContext context, string? key, string? token)
    {
        foreach (var (name, value) in SecurityHeaders)
        {
            context.Response.Headers[name] = value;
        }

        var services = context.RequestServices;
        var signer = services.GetRequiredService<LocalUrlSigner>();

        // The SYSTEM clock, deliberately not the host's TimeProvider: the expiry was stamped by
        // LocalStorageProvider.GetPresignedUrlAsync from DateTimeOffset.UtcNow, and signing and checking must
        // read the same clock. A host that registers another TimeProvider — a FakeTimeProvider in its
        // integration tests starts in the year 2000 — would otherwise make every expired link valid for ever,
        // or, with a clock running ahead, refuse every link.
        var now = DateTimeOffset.UtcNow;

        // One answer for every rejection, so a prober learns nothing about which part was wrong.
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token) ||
            !signer.TryVerify(key, PresignedUrlOperation.Get, token, now))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var read = await services.GetRequiredService<IStorageProvider>()
            .GetAsync(key, context.RequestAborted).ConfigureAwait(false);
        return read is null ? Results.NotFound() : Results.Stream(read.Content, read.ContentType);
    }
}
