using System.Security.Cryptography;
using System.Text;
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
public sealed class RefreshTokenService : IRefreshTokenService, ISessionRevoker
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

        // The presented hash IS the proof (256 random bits), so the owner is the token row's own user, found
        // in whatever tenant it belongs to. A refresh request has no JWT, so under a claims-only tenant
        // strategy the ambient scope is null and a scoped lookup would never find a tenant user. Nothing
        // here accepts a tenant or a user id from the caller; the caller's scope is not consulted.
        var user = await IdentityScope.ResolveUserAnyTenantAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshValidationResult.Invalid();
        }

        var now = timeProvider.GetUtcNow();

        // Reuse check precedes expiry on purpose: a consumed/revoked token replayed by a thief must
        // revoke the family even if it has since expired.
        if (match.ConsumedAt is not null || match.RevokedAt is not null)
        {
            if (await TryReplayAsync(match, rawToken, user, now, cancellationToken).ConfigureAwait(false) is { } replay)
            {
                return replay;
            }

            logger.LogWarning("Refresh token reuse detected; revoking token family {FamilyId} for user {UserId}.", match.FamilyId, match.UserId);
            await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
            return RefreshValidationResult.ReuseDetected();
        }

        if (match.ExpiresAt <= now)
        {
            return RefreshValidationResult.Invalid();
        }

        // A deactivated user's token is useless even if a revoke was missed. Refused before rotating, so the
        // token is not consumed and no successor exists. A platform user is refused the same way when platform
        // login is off, so turning the option off ends sessions opened while it was on (they live for the refresh
        // lifetime otherwise).
        if (!user.IsActive || (user.TenantId is null && !options.AllowPlatformLogin))
        {
            logger.LogWarning("Refresh refused for user {UserId}: account inactive or platform login disabled.", user.Id);
            return RefreshValidationResult.Invalid();
        }

        var (successor, raw) = Create(match.UserId, match.FamilyId);
        // Compare-and-set: the successor carries the parent's id in replaced_token_id, which has a filtered
        // unique index. Two simultaneous rotations of the same token both try to insert a successor with the
        // same replaced_token_id; the index lets only one commit. The loser hits a unique violation in the
        // transaction below. Without the reuse grace it returns Invalid (the legitimate winner already rotated);
        // with it, it takes the replay path and gets the winner's successor.
        successor.ReplacedTokenId = match.Id;
        var graceOn = options.RefreshReuseGrace > TimeSpan.Zero;
        try
        {
            // In a transaction so that a lost race also DISCARDS the change tracker (EF keeps the failed entities as
            // Modified/Added otherwise, and the loser's re-read below would see its own uncommitted values).
            await unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    await tokens.AddAsync(successor, ct).ConfigureAwait(false);
                    match.ConsumedAt = now;
                    match.ReplacedById = successor.Id;
                    if (graceOn)
                    {
                        match.SuccessorCiphertext = RefreshSuccessorProtector.Protect(rawToken, match.Id, raw);
                    }

                    tokens.Update(match);
                },
                cancellationToken).ConfigureAwait(false);
        }
        // ConcurrencyException is deliberately NOT caught here: RefreshToken carries no optimistic-concurrency
        // token today, so the parent UPDATE always affects its row and the filtered unique index on
        // replaced_token_id is the sole race gate. If RefreshToken later gains a row-version (issue #86
        // follow-up), this catch must also handle ConcurrencyException. Do not add that clause until then —
        // it would handle a currently-impossible scenario.
        catch (UniqueConstraintException)
        {
            // Lost the race. The tracker was cleared by the failed transaction, so this read sees the winner's row.
            return await ReplayAfterLostRaceAsync(rawToken, hash, user, cancellationToken).ConfigureAwait(false);
        }

        if (graceOn)
        {
            await ClearAgedSuccessorCiphertextsAsync(match.UserId, now, cancellationToken).ConfigureAwait(false);
        }

        return RefreshValidationResult.Success(user, new RefreshIssue(raw, successor.ExpiresAt, successor.FamilyId));
    }

    /// <inheritdoc />
    public async Task<Guid?> ResolveOwnerAsync(string rawToken, CancellationToken cancellationToken = default) =>
        (await FindOwnerAsync(rawToken, cancellationToken).ConfigureAwait(false))?.UserId;

    /// <inheritdoc />
    public async Task<RefreshTokenOwner?> FindOwnerAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return null;
        }

        var user = await IdentityScope.ResolveUserAnyTenantAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        return user is null ? null : new RefreshTokenOwner(user.Id, user.TenantId);
    }

    /// <inheritdoc />
    public async Task<int> RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return 0;
        }

        // Token-proven, like rotation: the owner is found without the caller's scope, so a logout request
        // with no JWT revokes a tenant user's session instead of silently doing nothing.
        var user = await IdentityScope.ResolveUserAnyTenantAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        if (allForUser)
        {
            // One set-based UPDATE over the user's active tokens (the spec already filters
            // RevokedAt == null && ExpiresAt > now), instead of loading-and-stamping each row.
            var revoked = await tokens.UpdateWhereAsync(
                new ActiveRefreshTokensByUserSpec(match.UserId, now),
                set => set.Set(t => t.RevokedAt, now),
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Revoked all active refresh tokens for user {UserId} ({Count} tokens).", match.UserId, revoked);
            return revoked;
        }

        return await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForUserExceptAsync(Guid userId, string keepSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keepSessionId);
        if (!Guid.TryParse(keepSessionId, out var keepFamilyId))
        {
            throw new ArgumentException("The session id does not name a session.", nameof(keepSessionId));
        }

        // Resolve the owner in scope first, as RevokeAllForUserAsync does: a user outside it revokes nothing.
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            logger.LogWarning(
                "RevokeAllForUserExcept revoked nothing: user {UserId} is not visible in the current tenant scope.", userId);
            return 0;
        }

        var now = timeProvider.GetUtcNow();

        // The kept id must be a live session of THIS user. Anything else (an unknown id, another user's family, a
        // revoked or expired one) would keep nothing and sign the caller out, so refuse it and let the app fall back.
        if (!await tokens.AnyAsync(new ActiveRefreshTokensByUserAndFamilySpec(user.Id, keepFamilyId, now), cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("The session id does not name an active session of the user.", nameof(keepSessionId));
        }

        var revoked = await tokens.UpdateWhereAsync(
            new ActiveRefreshTokensByUserExceptFamilySpec(user.Id, keepFamilyId, now),
            set => set.Set(t => t.RevokedAt, now),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Revoked {Count} refresh tokens for user {UserId}, keeping one session.", revoked, user.Id);
        return revoked;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Resolve the owner in scope first, as every other operation here does: a user id from another
        // tenant revokes nothing rather than ending that tenant's sessions.
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            // Not an error and not a success: 0 here can mean "no such user" or "a user in another tenant's
            // scope". Callers that mean to end a session (a user-mutation hook, an admin action) must not
            // read it as done, so it is loud. Token-proven logout does not come through here.
            logger.LogWarning(
                "RevokeAllForUser revoked nothing: user {UserId} is not visible in the current tenant scope.", userId);
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var revoked = await tokens.UpdateWhereAsync(
            new ActiveRefreshTokensByUserSpec(user.Id, now),
            set => set.Set(t => t.RevokedAt, now),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Revoked all active refresh tokens for user {UserId} ({Count} tokens).", user.Id, revoked);
        return revoked;
    }

    // Hygiene, not security (a sealed value needs the rotated token's raw value to open): once the grace has passed nothing can replay a rotated
    // token, so clear the sealed successors of this user's older rotations. Best-effort and outside the rotation transaction: a failure here must never
    // fail a refresh that already succeeded.
    private async Task ClearAgedSuccessorCiphertextsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await tokens.UpdateWhereAsync(
                new AgedSuccessorCiphertextByUserSpec(userId, now - options.RefreshReuseGrace),
                set => set.Set(t => t.SuccessorCiphertext, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Clearing aged sealed refresh successors failed for user {UserId}; the next rotation retries.", userId);
        }
    }

    // Reuse grace: a token that was just rotated and is presented again within RefreshReuseGrace gets the successor it already produced.
    // Every condition fails closed into the caller's reuse path (revoke the family).
    private async Task<RefreshValidationResult?> TryReplayAsync(
        RefreshToken rotated, string rawToken, User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grace = options.RefreshReuseGrace;
        if (grace <= TimeSpan.Zero
            || rotated.RevokedAt is not null
            || rotated.ConsumedAt is not { } consumedAt
            || now - consumedAt > grace
            || rotated.ReplacedById is not { } successorId
            || string.IsNullOrEmpty(rotated.SuccessorCiphertext)
            || !user.IsActive
            || (user.TenantId is null && !options.AllowPlatformLogin))
        {
            return null;
        }

        var successor = await tokens.FirstOrDefaultAsync(new RefreshTokenByIdSpec(successorId), cancellationToken).ConfigureAwait(false);
        if (successor is null
            || successor.FamilyId != rotated.FamilyId
            || successor.UserId != rotated.UserId
            || successor.ConsumedAt is not null
            || successor.RevokedAt is not null
            || successor.ExpiresAt <= now)
        {
            return null;
        }

        var successorRaw = RefreshSuccessorProtector.Unprotect(rawToken, rotated.Id, rotated.SuccessorCiphertext);
        if (successorRaw is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(TokenHasher.Hash(successorRaw)), Encoding.UTF8.GetBytes(successor.TokenHash)))
        {
            return null;
        }

        logger.LogInformation("Refresh token replayed within the reuse grace; returning the existing successor (family {FamilyId}, user {UserId}).", rotated.FamilyId, rotated.UserId);
        return RefreshValidationResult.ReplayedWithinGrace(user, new RefreshIssue(successorRaw, successor.ExpiresAt, successor.FamilyId));
    }

    private async Task<RefreshValidationResult> ReplayAfterLostRaceAsync(string rawToken, string hash, User user, CancellationToken cancellationToken)
    {
        if (options.RefreshReuseGrace <= TimeSpan.Zero)
        {
            return RefreshValidationResult.Invalid();
        }

        var rotated = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (rotated is null)
        {
            return RefreshValidationResult.Invalid();
        }

        return await TryReplayAsync(rotated, rawToken, user, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)
            ?? RefreshValidationResult.Invalid();
    }

    private async Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One set-based UPDATE over the family's not-yet-revoked tokens — the spec's RevokedAt == null
        // predicate means already-revoked rows are not needlessly rewritten.
        return await tokens.UpdateWhereAsync(
            new ActiveRefreshTokensByFamilySpec(familyId),
            set => set.Set(t => t.RevokedAt, now).Set(t => t.SuccessorCiphertext, null),
            cancellationToken).ConfigureAwait(false);
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
