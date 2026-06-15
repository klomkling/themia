using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IRefreshTokenService"/>. Persists only token hashes; raw tokens are
/// returned once. Resolves the owning user in scope before any read or write — a cross-tenant token
/// can never be rotated, revoked, or accepted (its row may be read by hash, but is rejected once the
/// owner fails to resolve in scope). Rotation chains a family; replaying a consumed/revoked token
/// revokes the family.</summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private const int TokenByteLength = 32;

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<RefreshToken, Guid> tokens;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;
    private readonly ILogger<RefreshTokenService> logger;

    /// <summary>Creates the service.</summary>
    public RefreshTokenService(
        IRepository<User, Guid> users,
        IRepository<RefreshToken, Guid> tokens,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IdentityModuleOptions options,
        ILogger<RefreshTokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.users = users;
        this.tokens = tokens;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"User '{userId}' was not found in the current tenant scope.");

        var (entity, raw) = Create(user.Id, Guid.CreateVersion7());
        await tokens.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshIssue(raw, entity.ExpiresAt, entity.FamilyId);
    }

    /// <inheritdoc />
    public async Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return RefreshValidationResult.Invalid();
        }

        // Resolve the owning user in scope BEFORE acting on the token, so a token whose owner is not in
        // the caller's tenant/platform scope is never read-acted-upon or written (no cross-tenant revoke).
        var user = await IdentityScope.ResolveUserAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshValidationResult.Invalid();
        }

        var now = timeProvider.GetUtcNow();

        // Reuse check precedes expiry on purpose: a consumed/revoked token replayed by a thief must
        // revoke the family even if it has since expired.
        if (match.ConsumedAt is not null || match.RevokedAt is not null)
        {
            logger.LogWarning("Refresh token reuse detected; revoking token family {FamilyId} for user {UserId}.", match.FamilyId, match.UserId);
            await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
            return RefreshValidationResult.ReuseDetected();
        }

        if (match.ExpiresAt <= now)
        {
            return RefreshValidationResult.Invalid();
        }

        var (successor, raw) = Create(match.UserId, match.FamilyId);
        // Compare-and-set: the successor carries the parent's id in replaced_token_id, which has a filtered
        // unique index. Two simultaneous rotations of the same token both try to insert a successor with the
        // same replaced_token_id; the index lets only one commit. The loser hits a unique violation here —
        // caught below — and returns Invalid (the legitimate winner already rotated).
        successor.ReplacedTokenId = match.Id;
        await tokens.AddAsync(successor, cancellationToken).ConfigureAwait(false);
        match.ConsumedAt = now;
        match.ReplacedById = successor.Id;
        tokens.Update(match);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintException)
        {
            return RefreshValidationResult.Invalid();
        }

        return RefreshValidationResult.Success(user, new RefreshIssue(raw, successor.ExpiresAt, successor.FamilyId));
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return;
        }

        var user = await IdentityScope.ResolveUserAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (allForUser)
        {
            var active = await tokens.ListAsync(new ActiveRefreshTokensByUserSpec(match.UserId, now), cancellationToken).ConfigureAwait(false);
            foreach (var t in active)
            {
                t.RevokedAt = now;
                tokens.Update(t);
            }
            logger.LogInformation("Revoked all active refresh tokens for user {UserId} ({Count} tokens).", match.UserId, active.Count);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var t in await tokens.ListAsync(new RefreshTokensByFamilySpec(familyId), cancellationToken).ConfigureAwait(false))
        {
            if (t.RevokedAt is null)
            {
                t.RevokedAt = now;
                tokens.Update(t);
            }
        }
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private (RefreshToken Entity, string Raw) Create(Guid userId, Guid familyId)
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));
        var now = timeProvider.GetUtcNow();
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = TokenHasher.Hash(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.Add(options.RefreshTokenLifetime),
        };
        token.SetId(Guid.CreateVersion7());
        return (token, raw);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
