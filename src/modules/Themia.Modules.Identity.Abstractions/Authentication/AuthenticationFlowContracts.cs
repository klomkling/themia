namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>An issued access + refresh pair returned to the client.</summary>
/// <param name="AccessToken">The serialized JWT.</param>
/// <param name="ExpiresInSeconds">Access-token lifetime remaining, in seconds.</param>
/// <param name="RefreshToken">The opaque refresh token.</param>
public readonly record struct AuthTokens(string AccessToken, int ExpiresInSeconds, string RefreshToken);

/// <summary>The outcome of a login attempt. Every non-success collapses to a uniform 401 at the
/// boundary; the distinction exists for internal/audit use only.</summary>
public enum LoginOutcome
{
    /// <summary>Authenticated.</summary>
    Success,

    /// <summary>Unknown user, wrong password, or inactive account.</summary>
    InvalidCredentials,

    /// <summary>Account is locked out.</summary>
    LockedOut,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>The result of <see cref="IAuthenticationFlow.LoginAsync"/>.</summary>
public readonly record struct LoginResult
{
    private LoginResult(LoginOutcome outcome, AuthTokens? tokens)
    {
        Outcome = outcome;
        Tokens = tokens;
    }

    /// <summary>The outcome.</summary>
    public LoginOutcome Outcome { get; }

    /// <summary>The issued tokens on success; otherwise null.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>Whether the login succeeded.</summary>
    public bool Succeeded => Outcome == LoginOutcome.Success;

    /// <summary>Creates a success result.</summary>
    public static LoginResult Success(AuthTokens tokens) => new(LoginOutcome.Success, tokens);

    /// <summary>Creates an invalid-credentials result.</summary>
    public static LoginResult InvalidCredentials() => new(LoginOutcome.InvalidCredentials, null);

    /// <summary>Creates a locked-out result.</summary>
    public static LoginResult LockedOut() => new(LoginOutcome.LockedOut, null);

    /// <summary>Creates a denied result.</summary>
    public static LoginResult Denied() => new(LoginOutcome.Denied, null);
}

/// <summary>The outcome of a refresh attempt. Every non-success collapses to a uniform 401.</summary>
public enum RefreshRotationOutcome
{
    /// <summary>Rotated; a new pair was issued.</summary>
    Success,

    /// <summary>Unknown, expired, or owner not in scope.</summary>
    Invalid,

    /// <summary>A consumed/revoked token was replayed; family revoked.</summary>
    ReuseDetected,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>The result of <see cref="IAuthenticationFlow.RefreshAsync"/>.</summary>
public readonly record struct RefreshRotationResult
{
    private RefreshRotationResult(RefreshRotationOutcome outcome, AuthTokens? tokens)
    {
        Outcome = outcome;
        Tokens = tokens;
    }

    /// <summary>The outcome.</summary>
    public RefreshRotationOutcome Outcome { get; }

    /// <summary>The issued tokens on success; otherwise null.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>Whether the refresh succeeded.</summary>
    public bool Succeeded => Outcome == RefreshRotationOutcome.Success;

    /// <summary>Creates a success result.</summary>
    public static RefreshRotationResult Success(AuthTokens tokens) => new(RefreshRotationOutcome.Success, tokens);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshRotationResult Invalid() => new(RefreshRotationOutcome.Invalid, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshRotationResult ReuseDetected() => new(RefreshRotationOutcome.ReuseDetected, null);

    /// <summary>Creates a denied result.</summary>
    public static RefreshRotationResult Denied() => new(RefreshRotationOutcome.Denied, null);
}

/// <summary>Orchestrates the security-critical login/refresh/logout sequence. The default
/// implementation lives in <c>Themia.Modules.Identity.AspNetCore</c> and is replaceable via DI.</summary>
public interface IAuthenticationFlow
{
    /// <summary>Verifies credentials (driving lockout + timing mitigation), builds the principal, and
    /// issues an access + refresh pair. Any failure returns a non-success result.</summary>
    /// <param name="userName">The login name.</param>
    /// <param name="password">The plaintext password.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The login result.</returns>
    Task<LoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Rotates a refresh token and mints a fresh access + refresh pair.</summary>
    /// <param name="refreshToken">The presented refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The rotation result.</returns>
    Task<RefreshRotationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the presented token's family (or all of the user's sessions). Idempotent.</summary>
    /// <param name="refreshToken">The presented refresh token.</param>
    /// <param name="allSessions">When true, revoke all of the user's sessions.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default);
}
