using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserService"/> over the Themia data abstractions.</summary>
public sealed class UserService : IUserService
{
    private readonly IRepository<User, Guid> users;
    private readonly IUnitOfWork unitOfWork;
    private readonly IPasswordHasher passwordHasher;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;
    private readonly IDataFilterScope filterScope;

    /// <summary>Creates the service.</summary>
    public UserService(
        IRepository<User, Guid> users,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        IdentityModuleOptions options,
        IDataFilterScope filterScope)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filterScope);
        this.users = users;
        this.unitOfWork = unitOfWork;
        this.passwordHasher = passwordHasher;
        this.timeProvider = timeProvider;
        this.options = options;
        this.filterScope = filterScope;
    }

    /// <inheritdoc />
    public async Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var normalizedName = IdentityScope.Normalize(userName);
        if (await users.AnyAsync(new UserByNormalizedNameSpec(normalizedName), cancellationToken).ConfigureAwait(false))
        {
            return UserCreationResult.Failure("duplicate_user_name");
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = IdentityScope.Normalize(email);
            if (await users.AnyAsync(new UserByNormalizedEmailSpec(normalizedEmail), cancellationToken).ConfigureAwait(false))
            {
                return UserCreationResult.Failure("duplicate_email");
            }
        }

        var user = new User
        {
            UserName = userName,
            NormalizedUserName = normalizedName,
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHasher.Hash(password),
        };
        user.SetId(Guid.CreateVersion7());

        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UserCreationResult.Success(user.Id);
    }

    /// <inheritdoc />
    public async Task<UserCreationResult> CreateExternalUserAsync(
        string userName, string? email, bool emailVerified, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var normalizedName = IdentityScope.Normalize(userName);
        if (await users.AnyAsync(new UserByNormalizedNameSpec(normalizedName), cancellationToken).ConfigureAwait(false))
        {
            return UserCreationResult.Failure("duplicate_user_name");
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = IdentityScope.Normalize(email);
            if (await users.AnyAsync(new UserByNormalizedEmailSpec(normalizedEmail), cancellationToken).ConfigureAwait(false))
            {
                return UserCreationResult.Failure("duplicate_email");
            }
        }

        var user = new User
        {
            UserName = userName,
            NormalizedUserName = normalizedName,
            Email = email,
            NormalizedEmail = normalizedEmail,
            EmailConfirmed = emailVerified,
            IsActive = true,
        };
        user.SetId(Guid.CreateVersion7());

        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UserCreationResult.Success(user.Id);
    }

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        users.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var normalized = IdentityScope.Normalize(userName);

        var inTenant = await users.FirstOrDefaultAsync(new UserByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
        if (inTenant is not null)
        {
            return inTenant;
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        return await users.FirstOrDefaultAsync(new PlatformUserByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalized = IdentityScope.Normalize(email);

        var inTenant = await users.FirstOrDefaultAsync(new UserByNormalizedEmailSpec(normalized), cancellationToken).ConfigureAwait(false);
        if (inTenant is not null)
        {
            return inTenant;
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        return await users.FirstOrDefaultAsync(new PlatformUserByNormalizedEmailSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = passwordHasher.Hash(password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        users.Update(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return PasswordVerificationResult.NotFound;
        }

        if (!user.IsActive)
        {
            return PasswordVerificationResult.Inactive;
        }

        var now = timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
        {
            return PasswordVerificationResult.LockedOut;
        }

        if (user.PasswordHash is null || !passwordHasher.Verify(user.PasswordHash, password))
        {
            if (user.LockoutEnabled)
            {
                user.AccessFailedCount++;
                if (user.AccessFailedCount >= options.MaxFailedAccessAttempts)
                {
                    user.LockoutEnd = now.Add(options.LockoutDuration);
                    user.AccessFailedCount = 0;
                }
                users.Update(user);
                await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
            }
            return PasswordVerificationResult.Failed;
        }

        // Success: clear failure state and re-hash if the cost parameters changed.
        var changed = false;
        if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            changed = true;
        }
        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = passwordHasher.Hash(password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (changed)
        {
            users.Update(user);
            await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        }

        return PasswordVerificationResult.Success;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        user.IsActive = isActive;
        users.Update(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        users.Remove(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
