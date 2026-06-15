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

    /// <summary>Creates a success result.</summary>
    public static RefreshValidationResult Success(User user, RefreshIssue replacement) =>
        new(RefreshOutcome.Success, user, replacement);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshValidationResult Invalid() => new(RefreshOutcome.Invalid, null, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshValidationResult ReuseDetected() => new(RefreshOutcome.ReuseDetected, null, null);
}

/// <summary>Issues, rotates, and revokes refresh tokens. All operations resolve the owning user in the
/// ambient tenant (else genuine platform) scope before reading or writing, so cross-tenant tokens are
/// never touched.</summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a new refresh token for a user, optionally continuing an existing family.</summary>
    /// <param name="userId">The owning user id (must resolve in scope).</param>
    /// <param name="familyId">An existing family to continue, or null to start a new one.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The issued token (raw value returned once).</returns>
    Task<RefreshIssue> IssueAsync(Guid userId, Guid? familyId = null, CancellationToken cancellationToken = default);

    /// <summary>Validates a presented raw token and, on success, consumes it and issues a successor in
    /// the same family. A replayed consumed/revoked token revokes the entire family.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the presented token's family, or all non-expired tokens for its owner.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="allForUser">When true, revoke every non-expired token for the owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default);
}
