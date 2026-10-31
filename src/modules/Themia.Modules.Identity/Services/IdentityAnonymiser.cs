using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IIdentityAnonymiser"/>: tombstones the user row and deletes its child rows in one
/// save.</summary>
public sealed class IdentityAnonymiser : IIdentityAnonymiser
{
    private const string TombstonePrefix = "anonymised-";

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<RefreshToken, Guid> refreshTokens;
    private readonly IRepository<UserToken, Guid> userTokens;
    private readonly IRepository<UserClaim, Guid> claims;
    private readonly IRepository<ExternalLoginLink, Guid> links;
    private readonly IRepository<OtpLoginChallenge, Guid> challenges;
    private readonly IUnitOfWork unitOfWork;
    private readonly IDataFilterScope filterScope;
    private readonly ITenantContext tenantContext;
    private readonly IUserLifecycleHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly ILogger<IdentityAnonymiser> logger;
    private readonly IMemoryCache? stampCache;

    /// <summary>Creates the service.</summary>
    public IdentityAnonymiser(
        IRepository<User, Guid> users,
        IRepository<RefreshToken, Guid> refreshTokens,
        IRepository<UserToken, Guid> userTokens,
        IRepository<UserClaim, Guid> claims,
        IRepository<ExternalLoginLink, Guid> links,
        IRepository<OtpLoginChallenge, Guid> challenges,
        IUnitOfWork unitOfWork,
        IDataFilterScope filterScope,
        ITenantContext tenantContext,
        IUserLifecycleHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        ILogger<IdentityAnonymiser>? logger = null,
        IMemoryCache? stampCache = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(userTokens);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(filterScope);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        this.users = users;
        this.refreshTokens = refreshTokens;
        this.userTokens = userTokens;
        this.claims = claims;
        this.links = links;
        this.challenges = challenges;
        this.unitOfWork = unitOfWork;
        this.filterScope = filterScope;
        this.tenantContext = tenantContext;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityAnonymiser>.Instance;
        this.stampCache = stampCache;
    }

    /// <inheritdoc />
    public async Task<AnonymiseResult> AnonymiseAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunAsync(userId, askHook: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            // Lost a race with a concurrent erasure of the same user: it deleted rows this call had loaded. The
            // failed save rolled back and cleared the tracker, so one re-run sees the tombstone and finishes
            // as AlreadyAnonymised. A second loss is not a race any more and surfaces. The veto hook already answered
            // on the first pass, so the re-run does not ask again.
            return await RunAsync(userId, askHook: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AnonymiseResult> RunAsync(Guid userId, bool askHook, CancellationToken cancellationToken)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        // ResolveUserAsync falls back to a platform row from a tenant scope; erasure is stricter: the user's
        // tenant must be the ambient one, so a tenant caller never reaches a platform user.
        if (user is null || !Equals(user.TenantId, tenantContext.CurrentTenantId))
        {
            return AnonymiseResult.NotFound();
        }

        var tombstone = TombstoneName(user.Id);
        var already = user.UserName == tombstone && user.Email is null && user.PhoneNumber is null && !user.IsActive && user.PasswordHash is null;
        if (!already && askHook)
        {
            var decision = await hooks.OnBeforeAnonymiseAsync(userId, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                await RaiseAsync((o, ct) => o.OnUserMutationRefusedAsync(userId, UserMutation.Anonymised, decision.Reason!, ct), cancellationToken).ConfigureAwait(false);
                return AnonymiseResult.Refused(decision.Reason!, decision.Code);
            }
        }

        // The user row and every child delete commit together or not at all, and a failed save clears the
        // tracker so the re-run in AnonymiseAsync starts clean. For a platform user the tenant write-validation
        // bypass covers the whole save, which is safe here because a platform scope sees only global rows, so every
        // staged child is global too.
        var oldPhone = user.NormalizedPhoneNumber;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (!already)
            {
                Scrub(user, tombstone);
                users.Update(user);
            }

            await RemoveChildrenAsync(user.Id, user.TenantId, oldPhone, ct).ConfigureAwait(false);
            await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        SecurityStampCache.Evict(stampCache, [user.Id]);

        if (already)
        {
            return AnonymiseResult.AlreadyAnonymised();
        }

        logger.LogInformation("Anonymised user {UserId}.", user.Id);
        await NotifyAsync(user, cancellationToken).ConfigureAwait(false);
        return AnonymiseResult.Anonymised();
    }

    private static string TombstoneName(Guid id) => $"{TombstonePrefix}{id:N}";

    private static void Scrub(User user, string tombstone)
    {
        user.UserName = tombstone;
        user.NormalizedUserName = IdentityScope.Normalize(tombstone);
        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.NormalizedPhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.PasswordHash = null;
        user.TwoFactorEnabled = false;
        user.IsActive = false;
        user.AccessFailedCount = 0;
        user.SecurityStamp = SecurityStampCache.NewStamp();
    }

    private async Task RemoveChildrenAsync(Guid userId, TenantId? tenantId, string? oldPhone, CancellationToken ct)
    {
        foreach (var token in await refreshTokens.ListAsync(new RefreshTokensByUserSpec(userId), ct).ConfigureAwait(false))
        {
            refreshTokens.Remove(token);
        }

        foreach (var token in await userTokens.ListAsync(new UserTokensByUserSpec(userId), ct).ConfigureAwait(false))
        {
            userTokens.Remove(token);
        }

        foreach (var claim in await claims.ListAsync(new UserClaimsByUserSpec(userId), ct).ConfigureAwait(false))
        {
            claims.Remove(claim);
        }

        foreach (var link in await links.ListAsync(new ExternalLoginsByUserSpec(userId), ct).ConfigureAwait(false))
        {
            links.Remove(link);
        }

        foreach (var challenge in await challenges.ListAsync(new OtpChallengesByUserOrPhoneSpec(userId, oldPhone, tenantId), ct).ConfigureAwait(false))
        {
            challenges.Remove(challenge);
        }
    }

    private async ValueTask NotifyAsync(User user, CancellationToken cancellationToken)
    {
        using (TenantContextAccessor.BeginScope(user.TenantId))
        {
            await hooks.OnUserMutatedAsync(user.Id, UserMutation.Anonymised, cancellationToken).ConfigureAwait(false);
            await RaiseAsync((o, ct) => o.OnUserMutatedAsync(user.Id, UserMutation.Anonymised, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Invokes every observer; a throwing one is logged and swallowed because the erasure is already
    /// committed (or refused) by the time they run.</summary>
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
}
