using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Themia.AspNetCore.Exceptions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Endpoints;

/// <summary>Login request body.</summary>
/// <param name="UserName">The login name.</param>
/// <param name="Password">The plaintext password.</param>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>Refresh request body.</summary>
/// <param name="RefreshToken">The opaque refresh token.</param>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Logout request body.</summary>
/// <param name="RefreshToken">The opaque refresh token.</param>
/// <param name="All">When true, revoke all of the user's sessions; default false.</param>
public sealed record LogoutRequest(string RefreshToken, bool All = false);

/// <summary>Issued token pair response.</summary>
/// <param name="AccessToken">The serialized JWT.</param>
/// <param name="ExpiresIn">Access-token lifetime remaining, in seconds.</param>
/// <param name="RefreshToken">The opaque refresh token.</param>
public sealed record AuthResponse(string AccessToken, int ExpiresIn, string RefreshToken);

/// <summary>Maps the opt-in login/refresh/logout endpoints. The host owns the route prefix
/// (e.g. <c>app.MapGroup("/auth").MapIdentityAuthEndpoints()</c>). Each endpoint is thin: it binds the
/// DTO and delegates to <see cref="IAuthenticationFlow"/>. Failures throw a uniform 401 through the
/// Themia ProblemDetails middleware.</summary>
public static class IdentityAuthEndpointRouteBuilderExtensions
{
    private const string GenericAuthFailure = "Invalid credentials.";

    /// <summary>Maps <c>POST login</c>, <c>POST refresh</c>, and <c>POST logout</c> relative to the
    /// calling route group.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapIdentityAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("login", LoginAsync);
        endpoints.MapPost("refresh", RefreshAsync);
        endpoints.MapPost("logout", LogoutAsync);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        // Boundary validation: a missing/blank credential is malformed input (400), not an auth
        // failure (401). An empty field is not an account-existence oracle, so this is not an
        // enumeration signal — actual credential failures still flow through to the uniform 401 below.
        if (request is null || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ValidationException(nameof(LoginRequest), "User name and password are required.");
        }

        var result = await flow.LoginAsync(request.UserName, request.Password, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetTokens(out var tokens))
        {
            throw new UnauthorizedException(GenericAuthFailure);
        }
        return Results.Ok(new AuthResponse(tokens.AccessToken, tokens.ExpiresInSeconds, tokens.RefreshToken));
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new ValidationException(nameof(RefreshRequest), "Refresh token is required.");
        }

        var result = await flow.RefreshAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetTokens(out var tokens))
        {
            throw new UnauthorizedException(GenericAuthFailure);
        }
        return Results.Ok(new AuthResponse(tokens.AccessToken, tokens.ExpiresInSeconds, tokens.RefreshToken));
    }

    private static async Task<IResult> LogoutAsync(LogoutRequest request, IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new ValidationException(nameof(LogoutRequest), "Refresh token is required.");
        }

        await flow.LogoutAsync(request.RefreshToken, request.All, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}
