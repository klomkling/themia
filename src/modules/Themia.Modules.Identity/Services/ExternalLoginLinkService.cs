using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IExternalLoginLinkService"/> over <c>identity.external_logins</c>.</summary>
public sealed class ExternalLoginLinkService : IExternalLoginLinkService
{
    private readonly IRepository<User, Guid> users;
    private readonly IRepository<ExternalLoginLink, Guid> links;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IDataFilterScope filterScope;
    private readonly IdentityModuleOptions options;
    private readonly IUserLifecycleHooks hooks;
    private readonly IIdentityEventObserver[] observers;
    private readonly ILogger<ExternalLoginLinkService> logger;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The user repository.</param>
    /// <param name="links">The external-login-link repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="filterScope">The data filter scope used for platform-aware writes.</param>
    /// <param name="options">The Identity module options (for the platform-login fallback).</param>
    /// <param name="hooks">The lifecycle hooks consulted before a link or unlink.</param>
    /// <param name="observers">The identity event observers told about each mutation and refusal.</param>
    /// <param name="logger">The logger; a throwing observer is logged here and swallowed.</param>
    public ExternalLoginLinkService(
        IRepository<User, Guid> users,
        IRepository<ExternalLoginLink, Guid> links,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IDataFilterScope filterScope,
        IdentityModuleOptions options,
        IUserLifecycleHooks hooks,
        IEnumerable<IIdentityEventObserver> observers,
        ILogger<ExternalLoginLinkService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(filterScope);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(observers);
        this.users = users;
        this.links = links;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.filterScope = filterScope;
        this.options = options;
        this.hooks = hooks;
        this.observers = observers as IIdentityEventObserver[] ?? observers.ToArray();
        // Optional for the same reason as UserService's: a required ILogger<T> in a host that never called
        // AddLogging would make this service unresolvable rather than merely quiet.
        this.logger = logger ?? NullLogger<ExternalLoginLinkService>.Instance;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginLinkResult> LinkAsync(
        Guid userId, ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Subject);

        var provider = identity.Provider.ToLowerInvariant();
        var subject = identity.Subject;

        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.UserNotFound);
        }

        if (!user.IsActive || user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.UserInactive);
        }

        // Existing ownership is answered BEFORE the hook, unlike UserService's mutations. Re-linking an
        // identity the user already holds writes nothing, so it is not a mutation to vet — and a consumer's
        // "one identity per provider" hook would otherwise refuse the idempotent retry of a link it already
        // allowed, because the user now does hold one for that provider.
        if (await OwnerOfAsync(provider, subject, cancellationToken).ConfigureAwait(false) is { } ownerId)
        {
            return new ExternalLoginLinkResult(ownerId == user.Id
                ? ExternalLoginLinkOutcome.AlreadyLinkedToUser
                : ExternalLoginLinkOutcome.LinkedToAnotherUser);
        }

        var decision = await hooks.OnBeforeLinkExternalLoginAsync(user.Id, provider, subject, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync(
                (o, ct) => o.OnUserMutationRefusedAsync(user.Id, UserMutation.ExternalLogin, decision.Reason!, ct),
                cancellationToken).ConfigureAwait(false);
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.Refused, decision.Reason);
        }

        try
        {
            // In a transaction so a lost insert race rolls back and, on EF, clears the tracked link —
            // otherwise the next save in this scope would try to insert it again.
            await unitOfWork.ExecuteInTransactionAsync(
                ct => InsertLinkAsync(user, provider, subject, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintException)
        {
            // Lost a race on (tenant, provider, external_id): the winner is committed and visible now. Answer
            // with whoever won — which may be this same user, from a concurrent request of their own.
            var winner = await OwnerOfAsync(provider, subject, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Linking '{provider}:{subject}' hit a unique violation, but no link exists afterwards.");
            return new ExternalLoginLinkResult(winner == user.Id
                ? ExternalLoginLinkOutcome.AlreadyLinkedToUser
                : ExternalLoginLinkOutcome.LinkedToAnotherUser);
        }

        await AnnounceAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.Linked);
    }

    /// <inheritdoc />
    public async Task<ExternalLoginUnlinkResult> UnlinkAsync(
        Guid userId, string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var normalized = provider.ToLowerInvariant();

        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new ExternalLoginUnlinkResult(ExternalLoginUnlinkOutcome.UserNotFound);
        }

        var held = await LinksOfAsync(user, normalized, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return new ExternalLoginUnlinkResult(ExternalLoginUnlinkOutcome.NotLinked);
        }

        var decision = await hooks.OnBeforeUnlinkExternalLoginAsync(user.Id, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RaiseAsync(
                (o, ct) => o.OnUserMutationRefusedAsync(user.Id, UserMutation.ExternalLogin, decision.Reason!, ct),
                cancellationToken).ConfigureAwait(false);
            return new ExternalLoginUnlinkResult(ExternalLoginUnlinkOutcome.Refused, decision.Reason);
        }

        foreach (var link in held)
        {
            links.Remove(link);
        }

        // Every pending change is one of this user's links, so all share its tenant: the platform-write bypass
        // SaveScopedAsync applies to the whole unit of work cannot reach another tenant's row here.
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken)
            .ConfigureAwait(false);

        await AnnounceAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new ExternalLoginUnlinkResult(ExternalLoginUnlinkOutcome.Unlinked);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return [];
        }

        var held = await LinksOfAsync(user, provider: null, cancellationToken).ConfigureAwait(false);
        return held.Select(ToInfo).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ExternalLoginInfo>>> GetLoginsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var distinct = userIds.Distinct().ToArray();
        if (distinct.Length > IExternalLoginLinkService.MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(userIds), distinct.Length,
                $"At most {IExternalLoginLinkService.MaxBatchSize} distinct user ids per call.");
        }

        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<ExternalLoginInfo>>();
        }

        var found = new List<ExternalLoginLink>(
            await links.ListAsync(new ExternalLoginsByUsersSpec(distinct), cancellationToken).ConfigureAwait(false));

        // The same reach a single lookup has: genuine platform users' links, when platform login is allowed.
        // Under a platform ambient scope the first query already returned them, so de-duplicate by id.
        if (options.AllowPlatformLogin)
        {
            var seen = found.Select(l => l.Id).ToHashSet();
            var platform = await links.ListAsync(new PlatformExternalLoginsByUsersSpec(distinct), cancellationToken)
                .ConfigureAwait(false);
            found.AddRange(platform.Where(l => seen.Add(l.Id)));
        }

        return found
            .GroupBy(l => l.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ExternalLoginInfo>)g.OrderBy(l => l.CreatedAt).Select(ToInfo).ToArray());
    }

    /// <inheritdoc />
    public Task<User?> FindUserByLoginAsync(
        string provider, string subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return ExternalLoginLookup.FindUserAsync(
            users, links, options, provider.ToLowerInvariant(), subject, cancellationToken);
    }

    /// <summary>The id of the user a (provider, subject) link belongs to, or null when unlinked.</summary>
    private async Task<Guid?> OwnerOfAsync(string provider, string subject, CancellationToken cancellationToken)
    {
        var found = await ExternalLoginLookup.FindLinkAsync(links, options, provider, subject, cancellationToken)
            .ConfigureAwait(false);
        return found?.Link.UserId;
    }

    /// <summary>The user's links, optionally for one provider, oldest first — read the way the user resolved.</summary>
    private Task<IReadOnlyList<ExternalLoginLink>> LinksOfAsync(
        User user, string? provider, CancellationToken cancellationToken) =>
        user.TenantId is null
            ? links.ListAsync(new PlatformExternalLoginsByUserSpec(user.Id, provider), cancellationToken)
            : links.ListAsync(new ExternalLoginsByUserSpec(user.Id, provider), cancellationToken);

    private async Task InsertLinkAsync(User user, string provider, string subject, CancellationToken cancellationToken)
    {
        var link = new ExternalLoginLink
        {
            UserId = user.Id,
            Provider = provider,
            ExternalId = subject,
            TenantId = user.TenantId,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        link.SetId(Guid.CreateVersion7());

        await links.AddAsync(link, cancellationToken).ConfigureAwait(false);
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AnnounceAsync(Guid userId, CancellationToken cancellationToken)
    {
        await hooks.OnUserMutatedAsync(userId, UserMutation.ExternalLogin, cancellationToken).ConfigureAwait(false);
        await RaiseAsync((o, ct) => o.OnUserMutatedAsync(userId, UserMutation.ExternalLogin, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Invokes <paramref name="invoke"/> on every observer. A throwing observer must not change the
    /// outcome it observes — which is already committed — so each is caught and logged, as in UserService.</summary>
    private async ValueTask RaiseAsync(
        Func<IIdentityEventObserver, CancellationToken, Task> invoke, CancellationToken cancellationToken)
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

    private static ExternalLoginInfo ToInfo(ExternalLoginLink link) =>
        new(link.Provider, link.ExternalId, link.CreatedAt);
}
