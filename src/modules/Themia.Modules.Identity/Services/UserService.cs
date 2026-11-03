using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
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
    private readonly IPhoneNumberNormalizer phoneNormalizer;
    private readonly IUserLifecycleHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly ILogger<UserService> logger;
    private readonly IMemoryCache? stampCache;

    /// <summary>Creates the service.</summary>
    public UserService(
        IRepository<User, Guid> users,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        IdentityModuleOptions options,
        IDataFilterScope filterScope,
        IPhoneNumberNormalizer phoneNormalizer,
        IUserLifecycleHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        ILogger<UserService>? logger = null,
        IMemoryCache? stampCache = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filterScope);
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        this.users = users;
        this.unitOfWork = unitOfWork;
        this.passwordHasher = passwordHasher;
        this.timeProvider = timeProvider;
        this.options = options;
        this.filterScope = filterScope;
        this.phoneNormalizer = phoneNormalizer;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        // Defaults to NullLogger rather than requiring ILogger<UserService> to be registered: in a host
        // that never called AddLogging, Microsoft DI would otherwise silently fall back to the 8-parameter
        // back-compat constructor below, and every OnUserMutatedAsync event would disappear with no error.
        // A default parameter keeps this constructor always resolvable, so it is always the one DI picks.
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UserService>.Instance;
        this.stampCache = stampCache;
    }

    /// <summary>
    /// Back-compatible overload matching the pre-<see cref="IIdentityEventObserver"/> signature. Forwards
    /// with an empty observer collection and a null logger, so a manually constructed instance — e.g. in
    /// an adopter's own test — keeps compiling and behaves exactly as before. DI resolves the greediest
    /// constructor it can satisfy, and both new parameters are always resolvable (an empty
    /// <see cref="IEnumerable{T}"/>, and logging via <c>AddLogging</c>), so a live app always picks the
    /// constructor above.
    /// </summary>
    public UserService(
        IRepository<User, Guid> users,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        IdentityModuleOptions options,
        IDataFilterScope filterScope,
        IPhoneNumberNormalizer phoneNormalizer,
        IUserLifecycleHooks hooks)
        : this(users, unitOfWork, passwordHasher, timeProvider, options, filterScope, phoneNormalizer, hooks,
              Array.Empty<IIdentityEventObserver>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<UserService>.Instance)
    {
    }

    /// <summary>
    /// Announces a committed mutation to the lifecycle hook and the observers, running as the mutated user's
    /// own tenant. The caller's scope is whoever is calling (a platform admin, an anonymous reset or confirm
    /// request); a hook that revokes the user's sessions or writes tenant data must act on the user's tenant,
    /// not the caller's. The tenant comes from the user row this service just loaded and changed, and the
    /// caller's scope is restored afterwards even if a hook throws.
    /// </summary>
    private async ValueTask NotifyMutatedAsync(User user, UserMutation mutation, CancellationToken cancellationToken)
    {
        using (TenantContextAccessor.BeginScope(user.TenantId))
        {
            await hooks.OnUserMutatedAsync(user.Id, mutation, cancellationToken).ConfigureAwait(false);
            await RaiseAsync((o, ct) => o.OnUserMutatedAsync(user.Id, mutation, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Invokes <paramref name="invoke"/> against every registered observer. A throwing observer
    /// must not change the flow it observes, so each invocation is individually caught and logged at
    /// <c>Error</c> — including <see cref="OperationCanceledException"/>. By the time this runs, the
    /// flow's own outcome (success or failure) is already decided; an observer's own database write
    /// throwing OCE because the client disconnected must not turn that into an unhandled exception. The
    /// caller is not waiting on the observer, so there is nothing for the cancellation to usefully abort.</summary>
    private async ValueTask RaiseAsync(Func<IIdentityEventObserver, CancellationToken, Task> invoke, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await invoke(observer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identity event observer {ObserverType} threw and was swallowed.", observer.GetType());
            }
        }
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
        await NotifyMutatedAsync(user, UserMutation.Created, cancellationToken).ConfigureAwait(false);
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
        await NotifyMutatedAsync(user, UserMutation.Created, cancellationToken).ConfigureAwait(false);
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
    public async Task<User?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);

        // A number that normalizes to nothing is not "no filter" — it matches nothing. Returning null
        // here rather than querying keeps punctuation-only input from resolving to whichever row happens
        // to carry a null normalized form.
        var normalized = phoneNormalizer.Normalize(phoneNumber);
        if (normalized is null)
        {
            return null;
        }

        var inTenant = await users.FirstOrDefaultAsync(new UserByNormalizedPhoneSpec(normalized), cancellationToken).ConfigureAwait(false);
        if (inTenant is not null)
        {
            return inTenant;
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        return await users.FirstOrDefaultAsync(new PlatformUserByNormalizedPhoneSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> SetEmailAsync(Guid userId, string? email, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserMutationResult.UserNotFound();
        }

        // Before the duplicate probe, not after: a refusal is the consumer's rule and should not depend on
        // whether the module happened to reject the value first for its own reasons.
        var decision = await hooks.OnBeforeSetEmailAsync(userId, email, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Email, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalized = IdentityScope.Normalize(email);
            var holder = await users.FirstOrDefaultAsync(new UserByNormalizedEmailSpec(normalized), cancellationToken).ConfigureAwait(false);
            if (holder is not null && holder.Id != user.Id)
            {
                return UserMutationResult.Duplicate();
            }
        }

        user.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        user.NormalizedEmail = normalized;

        // Always cleared, including when the address did not change — same reasoning as the phone number
        // below: a confirmed email is a login identifier, so carrying confirmation across a write would
        // let a profile edit inherit someone else's confirmed status.
        user.EmailConfirmed = false;

        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyMutatedAsync(user, UserMutation.Email, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> ConfirmEmailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user?.NormalizedEmail is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeConfirmEmailAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.EmailConfirmation, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        user.EmailConfirmed = true;
        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyMutatedAsync(user, UserMutation.EmailConfirmation, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> SetPhoneNumberAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeSetPhoneNumberAsync(userId, phoneNumber, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Phone, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        var normalized = phoneNormalizer.Normalize(phoneNumber);
        if (normalized is not null)
        {
            var holder = await users.FirstOrDefaultAsync(new UserByNormalizedPhoneSpec(normalized), cancellationToken).ConfigureAwait(false);
            if (holder is not null && holder.Id != user.Id)
            {
                return UserMutationResult.Duplicate();
            }

            if (options.RejectPhoneNumberHeldInAnotherTenant)
            {
                var elsewhere = await users.ListAsync(new ActiveUsersByNormalizedPhoneAnyTenantSpec(normalized), cancellationToken).ConfigureAwait(false);
                if (elsewhere.Any(u => u.Id != user.Id && !Equals(u.TenantId, user.TenantId)))
                {
                    return UserMutationResult.Refused(
                        "That phone number is already in use by an active user in another tenant.",
                        IdentityModuleOptions.PhoneNumberInAnotherTenantRefusalCode);
                }
            }
        }

        user.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        user.NormalizedPhoneNumber = normalized;

        // Always cleared, including when the number did not change: confirmation is proof of control over
        // one number at one time. Carrying it across a write would let a profile edit inherit someone
        // else's confirmed status, which — once the number is a login identifier — is account takeover.
        user.PhoneNumberConfirmed = false;

        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyMutatedAsync(user, UserMutation.Phone, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> ConfirmPhoneNumberAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user?.NormalizedPhoneNumber is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeConfirmPhoneNumberAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.PhoneConfirmation, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        user.PhoneNumberConfirmed = true;
        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyMutatedAsync(user, UserMutation.PhoneConfirmation, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeSetPasswordAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Password, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        user.PasswordHash = passwordHasher.Hash(password);
        user.SecurityStamp = SecurityStampCache.NewStamp();
        // A reset proves ownership, so it also lifts a lock: otherwise a victim whose account was locked by someone
        // else's guesses would wait out the full (possibly progressive) lock after recovering.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        users.Update(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, [user.Id]);
        await NotifyMutatedAsync(user, UserMutation.Password, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <summary>Bound on gate retries per guess. Each lost round means another guess was charged, and a lock stops
    /// the loop, so a real threshold ends it in a few rounds; the bound only has to outlast the widest burst.</summary>
    private const int MaxFailureGateAttempts = 64;

    /// <inheritdoc />
    /// <remarks>
    /// The failure state is changed only by compare-and-set updates whose affected-row count is read as "this guess won":
    /// the store must report MATCHED rows, as PostgreSQL and SQL Server do. A MySQL connection configured to report only
    /// CHANGED rows (<c>UseAffectedRows=true</c>) would see 0 for an accept that changes nothing.
    /// </remarks>
    public async Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return PasswordVerificationResult.NotFound;
        }

        // A locked or inactive account answers without any hash work, as it always did.
        if (!user.IsActive)
        {
            return PasswordVerificationResult.Inactive;
        }

        if (user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return PasswordVerificationResult.LockedOut;
        }

        // The guess is judged once per password hash (Argon2 is the expensive part); every state change below is a
        // compare-and-set on the failure state read in the same round, so a parallel guess cannot overwrite a charge,
        // and no guess is answered Success on an account that locked while it was being judged.
        var judgedAgainst = user.PasswordHash;
        var matches = judgedAgainst is not null && passwordHasher.Verify(judgedAgainst, password);
        string? rehashed = null;

        for (var round = 0; round < MaxFailureGateAttempts; round++)
        {
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

            if (!string.Equals(user.PasswordHash, judgedAgainst, StringComparison.Ordinal))
            {
                // The password changed while the guess was being judged: judge it again against the new hash.
                judgedAgainst = user.PasswordHash;
                matches = judgedAgainst is not null && passwordHasher.Verify(judgedAgainst, password);
                rehashed = null;
            }

            if (matches && rehashed is null && user.PasswordHash is { } currentHash && passwordHasher.NeedsRehash(currentHash))
            {
                rehashed = passwordHasher.Hash(password);   // once per hash, not per round
            }

            // A wrong guess is decided once it has been judged: charging it, re-reading after a lost race and raising the
            // lock it triggers run on None, so a client that aborts here cannot turn it into a free, uncharged guess or
            // cost the lock its event. Nothing external runs in between. A right guess decides nothing until it is accepted,
            // so accepting (and its re-read) stays on the caller's token and the next login redoes it.
            var storeToken = matches ? cancellationToken : CancellationToken.None;
            var charged = matches
                ? await TryAcceptAsync(user, rehashed, storeToken).ConfigureAwait(false)
                : await TryChargeAsync(user, now, storeToken).ConfigureAwait(false);
            if (charged is { } result)
            {
                return result;
            }

            // Lost the slot to a parallel guess: read the account again and decide on its new state.
            user = await IdentityScope.ResolveUserAnyTenantAsync(users, user.Id, storeToken).ConfigureAwait(false);
        }

        logger.LogWarning("Failure-state gate retries exhausted for user {UserId}; answering LockedOut uncharged.", user?.Id);
        return PasswordVerificationResult.LockedOut;
    }

    // A wrong guess: charge it against the failure state the caller read. Null = lost the race, read again.
    private async Task<PasswordVerificationResult?> TryChargeAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!user.LockoutEnabled)
        {
            return PasswordVerificationResult.Failed;
        }

        var count = user.AccessFailedCount + 1;
        DateTimeOffset? lockEnd = null;
        var storedCount = count;
        if (count >= options.MaxFailedAccessAttempts)
        {
            lockEnd = now.Add(ComputeLockoutDuration(count));
            storedCount = options.LockoutPolicy == LockoutPolicy.ResetCountOnLock ? 0 : count;
        }

        var gate = new UserFailureStateGateSpec(user.Id, user.AccessFailedCount, user.LockoutEnd, null);
        var updated = lockEnd is { } end
            ? await users.UpdateWhereAsync(gate, set => set.Set(u => u.AccessFailedCount, storedCount).Set(u => u.LockoutEnd, (DateTimeOffset?)end), cancellationToken).ConfigureAwait(false)
            : await users.UpdateWhereAsync(gate, set => set.Set(u => u.AccessFailedCount, storedCount), cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            return null;
        }

        // No notification existed here before: an observer could previously only infer a lockout
        // from the NEXT login attempt returning LockedOut — a different event at a different time.
        if (lockEnd is { } lockedUntil)
        {
            await RaiseAsync((o, ct) => o.OnLockedOutAsync(user.Id, lockedUntil, ct), cancellationToken).ConfigureAwait(false);
        }

        return PasswordVerificationResult.Failed;
    }

    // A right guess: also passes the gate, so it cannot be answered Success on an account that locked meanwhile. It clears
    // the failure state and, when the cost parameters changed, re-hashes the same password (not a credential event: the
    // stamp stays and the user's other sessions live). The stamp is part of the gate so a re-hash can never restore an
    // old password over a reset that landed meanwhile. Null = lost the race, read again.
    private async Task<PasswordVerificationResult?> TryAcceptAsync(User user, string? rehashed, CancellationToken cancellationToken)
    {
        var gate = new UserFailureStateGateSpec(user.Id, user.AccessFailedCount, user.LockoutEnd, user.SecurityStamp);
        var evict = user.AccessFailedCount != 0 || user.LockoutEnd is not null || rehashed is not null;
        var updated = rehashed is null
            ? await users.UpdateWhereAsync(gate, set => set.Set(u => u.AccessFailedCount, 0).Set(u => u.LockoutEnd, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false)
            : await users.UpdateWhereAsync(gate, set => set.Set(u => u.AccessFailedCount, 0).Set(u => u.LockoutEnd, (DateTimeOffset?)null).Set(u => u.PasswordHash, rehashed), cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            return null;
        }

        if (evict)
        {
            SecurityStampCache.Evict(stampCache, [user.Id]);
        }

        return PasswordVerificationResult.Success;
    }

    // Under Progressive the lock number is derived from the kept failure count (count - threshold + 1), so the
    // sequence needs no column of its own. Math.Min on doubles keeps a huge count from overflowing the ticks.
    private TimeSpan ComputeLockoutDuration(int failedCount)
    {
        if (options.LockoutPolicy != LockoutPolicy.Progressive)
        {
            return options.LockoutDuration;
        }

        var doublings = failedCount - options.MaxFailedAccessAttempts;
        var scaledTicks = options.LockoutDuration.Ticks * Math.Pow(2, doublings);
        return TimeSpan.FromTicks((long)Math.Min(scaledTicks, options.MaxLockoutDuration.Ticks));
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeSetActiveAsync(userId, isActive, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Active, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        user.IsActive = isActive;
        user.SecurityStamp = SecurityStampCache.NewStamp();
        users.Update(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, [user.Id]);
        await NotifyMutatedAsync(user, UserMutation.Active, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }

    /// <inheritdoc />
    public async Task<UserMutationResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserMutationResult.UserNotFound();
        }

        var decision = await hooks.OnBeforeDeleteAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Deleted, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
            return UserMutationResult.Refused(decision.Reason!, decision.Code);
        }

        users.Remove(user);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, [user.Id]);
        await NotifyMutatedAsync(user, UserMutation.Deleted, cancellationToken).ConfigureAwait(false);
        return UserMutationResult.Success();
    }
}
