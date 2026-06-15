using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The real internal reason a login failed, supplied to <see cref="IAuthenticationHooks"/>
/// for audit. Never surfaced to the client (which sees a uniform 401).</summary>
public enum LoginFailureReason
{
    /// <summary>No matching user.</summary>
    NotFound,

    /// <summary>Password did not match.</summary>
    WrongPassword,

    /// <summary>Account disabled.</summary>
    Inactive,

    /// <summary>Account locked out.</summary>
    LockedOut,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>Base for hook contexts that can short-circuit the flow to a uniform 401 via
/// <see cref="Deny"/>.</summary>
public abstract class AuthenticationHookContext
{
    /// <summary>Whether a hook denied the operation.</summary>
    public bool IsDenied { get; private set; }

    /// <summary>The internal denial reason (for audit), if any.</summary>
    public string? DenialReason { get; private set; }

    /// <summary>Denies the operation. The client receives a uniform 401; the reason is for audit.</summary>
    /// <param name="reason">An optional internal reason.</param>
    public void Deny(string? reason = null)
    {
        IsDenied = true;
        DenialReason = reason;
    }
}

/// <summary>Pre-credential-verification gate (rate-limit, IP allowlist).</summary>
/// <param name="userName">The presented login name.</param>
public sealed class BeforeLoginContext(string userName) : AuthenticationHookContext
{
    /// <summary>The presented login name.</summary>
    public string UserName { get; } = userName;
}

/// <summary>Runs after verification, before tokens are issued (last-login stamp, post-auth gating).</summary>
/// <param name="user">The authenticated user.</param>
public sealed class LoginSucceededContext(User user) : AuthenticationHookContext
{
    /// <summary>The authenticated user.</summary>
    public User User { get; } = user;
}

/// <summary>Runs on any login failure with the real internal reason (audit only).</summary>
/// <param name="userName">The presented login name.</param>
/// <param name="reason">The real internal reason.</param>
public sealed class LoginFailedContext(string userName, LoginFailureReason reason)
{
    /// <summary>The presented login name.</summary>
    public string UserName { get; } = userName;

    /// <summary>The real internal reason.</summary>
    public LoginFailureReason Reason { get; } = reason;
}

/// <summary>Pre-rotation gate for refresh.</summary>
public sealed class BeforeRefreshContext : AuthenticationHookContext;

/// <summary>Runs after a successful rotation, before the new pair is returned.</summary>
/// <param name="user">The user whose token was rotated.</param>
public sealed class RefreshSucceededContext(User user) : AuthenticationHookContext
{
    /// <summary>The user whose token was rotated.</summary>
    public User User { get; } = user;
}

/// <summary>Runs after revocation.</summary>
/// <param name="allSessions">Whether all sessions were revoked.</param>
public sealed class LogoutContext(bool allSessions)
{
    /// <summary>Whether all sessions were revoked.</summary>
    public bool AllSessions { get; } = allSessions;
}

/// <summary>Before/after extension points the default <see cref="IAuthenticationFlow"/> invokes. The
/// default implementation is all no-ops; adopters override only what they need.</summary>
public interface IAuthenticationHooks
{
    /// <summary>Early login gate, before credential verification.</summary>
    /// <param name="context">The before-login context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default);

    /// <summary>After verification, before tokens are issued.</summary>
    /// <param name="context">The login-succeeded context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default);

    /// <summary>On any login failure, with the real internal reason.</summary>
    /// <param name="context">The login-failed context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default);

    /// <summary>Early refresh gate.</summary>
    /// <param name="context">The before-refresh context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default);

    /// <summary>After a successful rotation, before the new pair is returned.</summary>
    /// <param name="context">The refresh-succeeded context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default);

    /// <summary>After revocation.</summary>
    /// <param name="context">The logout context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLogoutAsync(LogoutContext context, CancellationToken cancellationToken = default);
}
