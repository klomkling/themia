using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The outcome of validating + rotating a refresh token.</summary>
public enum RefreshOutcome
{
    /// <summary>Rotated; a successor was issued.</summary>
    Success,

    /// <summary>Unknown, expired, or owner not in scope.</summary>
    Invalid,

    /// <summary>A consumed/revoked token was replayed; the family was revoked.</summary>
    ReuseDetected,
}

/// <summary>A newly issued refresh token. The raw value is returned exactly once.</summary>
/// <param name="RawToken">The opaque raw token (never persisted).</param>
/// <param name="ExpiresAt">Absolute expiry.</param>
/// <param name="FamilyId">The rotation family.</param>
public readonly record struct RefreshIssue(string RawToken, DateTimeOffset ExpiresAt, Guid FamilyId);

/// <summary>The result of <see cref="IRefreshTokenService.ValidateAndRotateAsync"/>.</summary>
public readonly record struct RefreshValidationResult
{
    private RefreshValidationResult(RefreshOutcome outcome, User? user, RefreshIssue? replacement)
    {
        Outcome = outcome;
        User = user;
        Replacement = replacement;
    }

    /// <summary>The outcome.</summary>
    public RefreshOutcome Outcome { get; }

    /// <summary>The resolved owning user on success; otherwise null.</summary>
    public User? User { get; }

    /// <summary>The successor refresh token on success; otherwise null.</summary>
    public RefreshIssue? Replacement { get; }

    /// <summary>Gets the resolved user and successor token when the rotation succeeded.</summary>
    /// <param name="user">The resolved owning user, when this returns <see langword="true"/>.</param>
    /// <param name="replacement">The successor refresh token, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the rotation succeeded.</returns>
    public bool TryGetSuccess([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out User? user, out RefreshIssue replacement)
    {
        user = User;
        replacement = Replacement ?? default;
        return Outcome == RefreshOutcome.Success;
    }

    /// <summary>Creates a success result.</summary>
    public static RefreshValidationResult Success(User user, RefreshIssue replacement) =>
        new(RefreshOutcome.Success, user, replacement);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshValidationResult Invalid() => new(RefreshOutcome.Invalid, null, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshValidationResult ReuseDetected() => new(RefreshOutcome.ReuseDetected, null, null);
}

/// <summary>The user a refresh token belongs to and the tenant that user lives in.</summary>
/// <param name="UserId">The owner.</param>
/// <param name="TenantId">The owner's tenant; <see langword="null"/> for a platform user when <paramref name="TenantKnown"/> is
/// <see langword="true"/>.</param>
/// <param name="TenantKnown"><see langword="false"/> when the implementation could name the owner but not the tenant (the
/// default <see cref="IRefreshTokenService.FindOwnerAsync"/>, which only knows <see cref="IRefreshTokenService.ResolveOwnerAsync"/>);
/// callers then keep the caller's tenant scope instead of switching to <paramref name="TenantId"/>.</param>
public sealed record RefreshTokenOwner(Guid UserId, Themia.Framework.Core.Abstractions.Tenancy.TenantId? TenantId, bool TenantKnown = true);

/// <summary>Issues, rotates, and revokes refresh tokens. Issuing, <c>RevokeAllForUserAsync</c> and the
/// user-id based operations resolve the user in the caller's ambient tenant (else genuine platform) scope. A
/// presented token is itself the proof of its owner: <c>ValidateAndRotateAsync</c>, <c>RevokeAsync</c>,
/// <c>ResolveOwnerAsync</c> and <c>FindOwnerAsync</c> find the owner in whatever tenant it belongs to, whatever
/// the caller's scope, so a refresh with no JWT works under a claims-only tenant strategy. A token can only ever
/// act as its own user in its own tenant.</summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a new refresh token for a user, always starting a new rotation family.</summary>
    /// <param name="userId">The owning user id (must resolve in scope).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The issued token (raw value returned once).</returns>
    Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Validates a presented raw token and, on success, consumes it and issues a successor in
    /// the same family. A replayed consumed/revoked token revokes the entire family.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the presented token's family, or all non-expired tokens for its owner. The token is the proof:
    /// the owner is found in whatever tenant it belongs to, independent of the caller's tenant scope.
    /// </summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="allForUser">When true, revoke every non-expired token for the owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many tokens were revoked. 0 means nothing changed: the token is unknown, its owner is
    /// gone, or its family was already revoked. Do not report a logout as done on 0.</returns>
    Task<int> RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active refresh token a user holds, without needing one of them in hand.</summary>
    /// <param name="userId">The owning user (must resolve in scope).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many tokens were revoked; 0 when the user does not resolve in scope.</returns>
    /// <remarks>
    /// For the moments a caller has no token to present — after unlinking a compromised external identity,
    /// on an administrator's "sign out everywhere". Sessions are not tagged by the identity that opened
    /// them, so this is the only way to end the one a compromised identity holds.
    /// <para>
    /// No default implementation, unlike <see cref="ResolveOwnerAsync"/>. A default that did nothing would
    /// let a caller believe every session had ended while all of them stayed valid; a custom implementation
    /// that cannot revoke should fail to compile rather than fail silently.
    /// </para>
    /// </remarks>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the id of the user who owns a presented raw token, without consuming, rotating,
    /// or revoking it. Used to attribute an audit event (e.g. logout) to a user before the token is
    /// acted upon.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The owning user's id, or <see langword="null"/> when the token does not resolve to a
    /// user in scope.</returns>
    /// <remarks>Default implementation returns <see langword="null"/>, so an existing custom
    /// <see cref="IRefreshTokenService"/> implementation keeps compiling without change — it simply
    /// does not attribute a user id to <see cref="LogoutContext"/> until it implements this method.</remarks>
    Task<Guid?> ResolveOwnerAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    /// <summary>
    /// Like <see cref="ResolveOwnerAsync"/>, but also reports the owner's tenant, so a caller can run what
    /// follows (logout hooks, audit) in that tenant's scope. The token is the proof; the lookup ignores the
    /// caller's tenant scope.
    /// </summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The owner, or <see langword="null"/> when the token is unknown or its owner is gone.</returns>
    /// <remarks>The default implementation answers from <see cref="ResolveOwnerAsync"/> with
    /// <see cref="RefreshTokenOwner.TenantKnown"/> <see langword="false"/>, so an existing custom implementation keeps
    /// compiling and is looked up once; the logout hooks then run in the caller's scope.</remarks>
    async Task<RefreshTokenOwner?> FindOwnerAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var userId = await ResolveOwnerAsync(rawToken, cancellationToken).ConfigureAwait(false);
        return userId is null ? null : new RefreshTokenOwner(userId.Value, null, TenantKnown: false);
    }
}
