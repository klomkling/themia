using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Themia.AspNetCore.Exceptions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Endpoints;

/// <summary>External-login request body. The client performs the OAuth/OIDC authorization-code step
/// itself and posts the resulting code (plus the redirect URI it used and an optional PKCE verifier).</summary>
/// <param name="Code">The authorization code obtained from the provider.</param>
/// <param name="RedirectUri">The redirect URI the client used for the authorization request.</param>
/// <param name="CodeVerifier">The PKCE code verifier, if PKCE was used.</param>
/// <param name="Nonce">The nonce the client sent in the authorization request, if any. When supplied,
/// the server requires the id_token's <c>nonce</c> claim to match it.</param>
public sealed record ExternalLoginRequest(string Code, string RedirectUri, string? CodeVerifier = null, string? Nonce = null);

/// <summary>Maps the opt-in external-login endpoint. The host owns the route prefix
/// (e.g. <c>app.MapGroup("/auth").MapIdentityExternalAuthEndpoints()</c>). The endpoint is thin: it binds
/// the DTO and delegates to <see cref="IExternalAuthenticationFlow"/>. Provider/exchange failures and
/// hook denials collapse to a uniform 401 through the Themia ProblemDetails middleware (only an unknown
/// provider yields 404), so the client cannot distinguish them.</summary>
public static class IdentityExternalAuthEndpointRouteBuilderExtensions
{
    private const string GenericAuthFailure = "Invalid credentials.";

    /// <summary>Maps <c>POST external/{provider}</c> relative to the calling route group.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapIdentityExternalAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("external/{provider}", ExternalLoginAsync);
        return endpoints;
    }

    private static async Task<IResult> ExternalLoginAsync(string provider, ExternalLoginRequest request, IExternalAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        // Boundary validation: a missing/blank code or redirect URI is malformed input (400), not an
        // auth failure (401). Genuine auth failures (provider rejection, hook deny) still flow to the
        // uniform 401 below; an unknown provider is the one exception (404).
        if (request is null || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            throw new ValidationException(nameof(ExternalLoginRequest), "Code and redirect URI are required.");
        }

        var externalRequest = new ExternalAuthRequest(request.Code, request.RedirectUri, request.CodeVerifier, request.Nonce);
        var result = await flow.AuthenticateAsync(provider, externalRequest, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && result.Tokens is { } tokens)
        {
            return Results.Ok(new AuthResponse(tokens.AccessToken, tokens.ExpiresInSeconds, tokens.RefreshToken));
        }

        if (result.Outcome == ExternalLoginOutcome.ProviderNotFound)
        {
            return Results.NotFound();
        }

        // ProviderRejected, Denied, and AccountInactive all collapse to the same uniform 401
        // (anti-enumeration); only the audit hook sees the distinct internal reason.
        throw new UnauthorizedException(GenericAuthFailure);
    }
}
