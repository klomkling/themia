using Microsoft.Extensions.Caching.Memory;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="ISecurityStampValidator"/>: reads the user in any tenant (the bearer check runs before
/// tenant resolution, and the id comes from a signature-verified token) and caches the answer in memory.</summary>
public sealed class SecurityStampValidator : ISecurityStampValidator
{
    private const string CacheKeyPrefix = "themia:identity:stamp:";

    private readonly IReadRepository<User, Guid> users;
    private readonly IMemoryCache cache;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the validator.</summary>
    public SecurityStampValidator(IReadRepository<User, Guid> users, IMemoryCache cache, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        this.users = users;
        this.cache = cache;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<bool> IsCurrentAsync(Guid userId, string? stamp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(stamp))
        {
            return false;
        }

        var current = await GetCurrentStampAsync(userId, cancellationToken).ConfigureAwait(false);
        return current is not null && string.Equals(current, stamp, StringComparison.Ordinal);
    }

    // Null means "no usable user" (unknown, deleted or inactive); it is cached like a stamp so a refused token
    // does not hit the store on every request.
    private async Task<string?> GetCurrentStampAsync(Guid userId, CancellationToken cancellationToken)
    {
        var window = options.SecurityStampCacheDuration;
        if (window <= TimeSpan.Zero)
        {
            return await LoadStampAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        var key = CacheKeyPrefix + userId.ToString("N");
        if (cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        var loaded = await LoadStampAsync(userId, cancellationToken).ConfigureAwait(false);
        cache.Set(key, loaded, window);
        return loaded;
    }

    private async Task<string?> LoadStampAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await IdentityScope.ResolveUserAnyTenantAsync(users, userId, cancellationToken).ConfigureAwait(false);
        return user is { IsActive: true } ? user.SecurityStamp : null;
    }
}
