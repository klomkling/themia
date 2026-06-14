using System.Security.Cryptography;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserTokenService"/>. Persists only token hashes; raw tokens are returned once.</summary>
public sealed class UserTokenService : IUserTokenService
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
        // Resolve the parent through the tenant-filtered repo (child table carries no tenant_id).
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
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

        // Resolve the parent through the tenant-filtered repo (child table carries no tenant_id).
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return TokenConsumeResult.NotFound;
        }

        var candidates = await tokens.ListAsync(new TokensByUserAndPurposeSpec(userId, purpose), cancellationToken).ConfigureAwait(false);
        var match = candidates.FirstOrDefault(t => TokenHasher.Matches(t.TokenHash, rawToken));
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

        match.ConsumedAt = timeProvider.GetUtcNow();
        tokens.Update(match);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TokenConsumeResult.Success;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
