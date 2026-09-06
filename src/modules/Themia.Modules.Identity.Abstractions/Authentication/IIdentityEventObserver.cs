namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>
/// Observes every authentication and account-lifecycle event across the password login flow, the
/// external-login flow, and user mutations — for audit, alerting, or metrics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate interface from <see cref="IAuthenticationHooks"/> / <see cref="Themia.Modules.Identity.Abstractions.Authentication.IExternalAuthenticationHooks"/>.</b>
/// Those are single-owner policy seams — registered <c>TryAddScoped</c>, resolved as exactly one instance,
/// and able to <c>Deny()</c> the operation. An audit consumer registering into either one either never
/// fires (its <c>TryAdd</c> loses to Identity's default) or silently replaces the adopter's own hooks.
/// Neither failure errors. <see cref="IIdentityEventObserver"/> is instead resolved as
/// <see cref="System.Collections.Generic.IEnumerable{T}"/>, so any number of consumers can observe the
/// same event without disturbing the single policy owner.
/// </para>
/// <para>
/// <b>Every event has a method, not a chosen few.</b> The rule <see cref="IUserLifecycleHooks"/> already
/// states for itself applies here too: a seam covering three of seven paths reads as covering all seven —
/// a consumer registers an observer, believes the audit trail is complete, and it silently is not on the
/// paths nobody wired (e.g. every password login present, a user signing in with Google leaving no trace
/// at all).
/// </para>
/// <para>
/// Every method has a default no-op body, so an adopter implementing one method is unaffected by the
/// rest, and an adopter registering zero observers gets today's behaviour exactly.
/// </para>
/// <para>
/// <b>Observation only — nothing here can deny.</b> A throwing observer must never change the flow it
/// observes: callers catch any exception, log it at <c>Error</c>, and continue. This matters twice over —
/// a failed audit write must never turn a successful login into a 500, and must never turn a *failed*
/// login into a different status code or body, which would leak the failure reason the uniform 401
/// exists to hide.
/// </para>
/// </remarks>
public interface IIdentityEventObserver
{
    // ── Password login ──────────────────────────────────────────────────────

    /// <summary>Raised after a password login succeeds and tokens are issued.</summary>
    /// <param name="userId">The authenticated user's id.</param>
    /// <param name="userName">The authenticated user's username.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLoginSucceededAsync(Guid userId, string userName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised on any login failure that was not a hook denial, with the real internal reason.</summary>
    /// <param name="userName">The presented login identifier (username, email, or phone).</param>
    /// <param name="reason">The real internal reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised when a login was refused by an adopter's own <see cref="IAuthenticationHooks"/>,
    /// either before or after credential verification.</summary>
    /// <param name="userName">The presented login identifier.</param>
    /// <param name="denialReason">The hook's optional internal denial reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLoginDeniedAsync(string userName, string? denialReason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // ── External login (Google / LINE / OIDC) ───────────────────────────────

    /// <summary>Raised after an external login succeeds and tokens are issued.</summary>
    /// <param name="userId">The resolved or provisioned user's id.</param>
    /// <param name="provider">The provider key (e.g. <c>google</c>, <c>line</c>).</param>
    /// <param name="wasCreated">Whether a new user was auto-provisioned from the provider identity.</param>
    /// <param name="wasLinked">Whether a new provider link was created against an existing account.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnExternalLoginSucceededAsync(
        Guid userId, string provider, bool wasCreated, bool wasLinked, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised on any external-login failure that was not a hook denial, with the real internal
    /// outcome.</summary>
    /// <param name="provider">The requested provider key.</param>
    /// <param name="reason">The real internal outcome.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnExternalLoginFailedAsync(string provider, ExternalLoginOutcome reason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised when an external login was refused by an adopter's own
    /// <see cref="Themia.Modules.Identity.Abstractions.Authentication.IExternalAuthenticationHooks"/>.</summary>
    /// <param name="provider">The requested provider key.</param>
    /// <param name="denialReason">The hook's optional internal denial reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnExternalLoginDeniedAsync(string provider, string? denialReason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // ── Refresh ──────────────────────────────────────────────────────────────

    /// <summary>Raised after a refresh token is rotated and a fresh pair is issued.</summary>
    /// <param name="userId">The user whose token was rotated.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnRefreshSucceededAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised when a refresh was refused by an adopter's own <see cref="IAuthenticationHooks"/>,
    /// either before or after rotation.</summary>
    /// <param name="userName">The owning user's username, when known (only resolved by the time the
    /// post-rotation gate runs; <see langword="null"/> for the pre-rotation gate).</param>
    /// <param name="denialReason">The hook's optional internal denial reason.</param>
    /// <param name="rotationCommitted">
    /// Whether the rotation had already persisted when the denial happened. When <see langword="true"/>,
    /// a valid successor refresh token exists that the client never received — the rotation is not rolled
    /// back. This is a materially different situation from <see langword="false"/> ("nothing happened")
    /// and calls for a different response.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnRefreshDeniedAsync(
        string? userName, string? denialReason, bool rotationCommitted, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised on a refresh failure that was not a hook denial: an invalid/expired/out-of-scope
    /// token, a detected reuse of an already-rotated token (the textbook signature of refresh-token
    /// theft), or a resolved account that is inactive or locked out.</summary>
    /// <param name="userId">
    /// The token's owning user id, when it is resolvable. Populated for
    /// <see cref="RefreshOutcome.ReuseDetected"/> — a rotated token replayed is the single most
    /// actionable event in this flow, and without the account an audit row has no other way to say whose
    /// session to revoke — and for the inactive/locked-out account case (also reported as
    /// <see cref="RefreshOutcome.Invalid"/>). <see langword="null"/> only for an unknown, expired, or
    /// out-of-scope token, which has no owner by construction.
    /// </param>
    /// <param name="outcome">The real internal outcome.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnRefreshFailedAsync(Guid? userId, RefreshOutcome outcome, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // ── Session and account ──────────────────────────────────────────────────

    /// <summary>Raised after a refresh token (or a user's full session set) is revoked.</summary>
    /// <param name="userId">The token's owning user id, resolved before revocation; <see langword="null"/>
    /// when the presented token did not resolve to a user in scope.</param>
    /// <param name="allSessions">Whether every session for the user was revoked.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised when an account is locked out after too many failed password attempts.</summary>
    /// <param name="userId">The locked-out user.</param>
    /// <param name="lockoutEnd">When the lockout expires.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>Without this, an observer can only infer a lockout from the *next* login attempt
    /// returning <see cref="LoginFailureReason.LockedOut"/> — a different event at a different time.</remarks>
    Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Raised after a credential-state mutation has been applied and saved.</summary>
    /// <param name="userId">The mutated user.</param>
    /// <param name="mutation">What changed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
