using System.Security.Cryptography;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserTokenService"/>. Persists only token hashes; raw tokens are returned once.</summary>
public sealed class UserTokenService : IUserTokenService, IUserTokenValidator
{
    private const int TokenByteLength = 32;

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<UserToken, Guid> tokens;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the service.</summary>
    public UserTokenService(
        IRepository<User, Guid> users,
        IRepository<UserToken, Guid> tokens,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.users = users;
        this.tokens = tokens;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(Guid userId, TokenPurpose purpose, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        // The parent must resolve in the ambient tenant or as a platform user; cross-tenant is refused.
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"User '{userId}' was not found in the current tenant scope.");
        }

        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));
        var token = new UserToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = TokenHasher.Hash(raw),
            ExpiresAt = timeProvider.GetUtcNow().Add(lifetime ?? options.DefaultTokenLifetime),
        };
        token.SetId(Guid.CreateVersion7());

        await tokens.AddAsync(token, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return raw;
    }

    /// <inheritdoc />
    public async Task<TokenConsumeResult> ConsumeAsync(Guid userId, TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        // The parent must resolve in the ambient tenant or as a platform user; cross-tenant is refused.
        if (!await IdentityScope.UserExistsAsync(users, userId, cancellationToken).ConfigureAwait(false))
        {
            return TokenConsumeResult.NotFound;
        }

        // TokenHasher.Hash is deterministic (plain SHA-256, no per-row salt), so an exact-match DB
        // lookup on the hash is correct and avoids loading every token for the pair (DoS-resistant).
        // The stored hash is high-entropy and irreversible — comparing it directly leaks nothing.
        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new TokenByUserPurposeAndHashSpec(userId, purpose, hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return TokenConsumeResult.NotFound;
        }

        if (match.ConsumedAt is not null)
        {
            return TokenConsumeResult.AlreadyConsumed;
        }

        if (match.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return TokenConsumeResult.Expired;
        }

        // Claimed with one conditional UPDATE (… WHERE consumed_at IS NULL AND expires_at > now) and the affected-row count
        // decides who won. Read-check-then-save let two concurrent callers both see an unconsumed row and both succeed, so a
        // single-use token (a second-factor ticket, a reset link) could be spent twice.
        var now = timeProvider.GetUtcNow();
        var claimed = await tokens.UpdateWhereAsync(
            new ClaimableUserTokenSpec(match.Id, now),
            set => set.Set(t => t.ConsumedAt, now),
            cancellationToken).ConfigureAwait(false);
        return claimed == 1
            ? TokenConsumeResult.Success
            : match.ExpiresAt <= now ? TokenConsumeResult.Expired : TokenConsumeResult.AlreadyConsumed;
    }

    /// <inheritdoc />
    public async Task<TokenValidationResult> ValidateAsync(TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        // The hash column is unique, so (purpose, hash) names at most one row; the token carries no user id.
        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new TokenByPurposeAndHashSpec(purpose, hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return new TokenValidationResult(TokenValidationOutcome.NotFound, null);
        }

        // The owner must resolve in the ambient tenant or as a platform user, as in consume; cross-tenant is not found.
        if (!await IdentityScope.UserExistsAsync(users, match.UserId, cancellationToken).ConfigureAwait(false))
        {
            return new TokenValidationResult(TokenValidationOutcome.NotFound, null);
        }

        if (match.ConsumedAt is not null)
        {
            return new TokenValidationResult(TokenValidationOutcome.AlreadyConsumed, null);
        }

        if (match.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return new TokenValidationResult(TokenValidationOutcome.Expired, null);
        }

        return new TokenValidationResult(TokenValidationOutcome.Valid, match.UserId);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
