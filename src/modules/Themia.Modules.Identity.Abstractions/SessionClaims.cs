using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>The session id claim carried by Themia access tokens, and readers for it. The session id is the id of the
/// refresh-token family the access token was minted for; it stays the same across refresh-token rotation, so it names
/// one sign-in (one device or browser) for as long as it lives.</summary>
public static class SessionClaims
{
    /// <summary>The claim type, the standard OpenID Connect <c>sid</c>.</summary>
    public const string SessionId = "sid";

    /// <summary>The caller's session id, or <see langword="null"/> for a token minted before session ids existed.
    /// Pass it to <see cref="Authentication.ISessionRevoker.RevokeAllForUserExceptAsync"/> to keep the current session.</summary>
    /// <param name="user">The current user.</param>
    public static string? GetSessionId(this ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Claims.FirstOrDefault(c => c.Type == SessionId)?.Value;
    }

    /// <summary>The principal's session id, or <see langword="null"/> when it has none.</summary>
    /// <param name="principal">The principal.</param>
    public static string? GetSessionId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst(SessionId)?.Value;
    }
}
