using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IExternalLoginService"/>. Resolves an external identity to a Themia
/// user under the ambient tenant scope: an existing (provider, subject) link returns its user; a
/// verified-email match auto-links the existing user; otherwise a password-less user is provisioned
/// and linked. The link lookup and create run under the framework tenant filter, so a link created in
/// one tenant is invisible to another and the same external account maps to a distinct user per
/// tenant. Auto-link happens only for a provider-asserted verified email.</summary>
public sealed class ExternalLoginService : IExternalLoginService
{
    private const int SubjectFallbackLength = 8;
    private const int DisambiguatorLength = 4;
    private const int MaxDisambiguationAttempts = 8;

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<ExternalLoginLink, Guid> links;
    private readonly IUserService userService;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IDataFilterScope filterScope;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The user repository.</param>
    /// <param name="links">The external-login-link repository.</param>
    /// <param name="userService">The user service used to provision password-less users.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="filterScope">The data filter scope used for platform-aware writes.</param>
    public ExternalLoginService(
        IRepository<User, Guid> users,
        IRepository<ExternalLoginLink, Guid> links,
        IUserService userService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IDataFilterScope filterScope)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(filterScope);
        this.users = users;
        this.links = links;
        this.userService = userService;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.filterScope = filterScope;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginResult> ResolveOrProvisionAsync(
        ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Subject);

        var provider = identity.Provider.ToLowerInvariant();

        // Existing link: the framework tenant filter isolates the lookup, so a link from another
        // tenant never matches.
        var link = await links
            .FirstOrDefaultAsync(new ExternalLoginByProviderKeySpec(provider, identity.Subject), cancellationToken)
            .ConfigureAwait(false);
        if (link is not null)
        {
            var linked = await IdentityScope.ResolveUserAsync(users, link.UserId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"External link '{provider}:{identity.Subject}' references user '{link.UserId}', which does not resolve in scope.");
            return new ExternalLoginResult(linked, WasCreated: false, WasLinked: false);
        }

        // Auto-link by verified email only: an unverified email must never adopt an existing account.
        if (!string.IsNullOrWhiteSpace(identity.Email) && identity.EmailVerified)
        {
            // FindByEmailAsync may resolve a platform (TenantId == null) user when AllowPlatformLogin is
            // set. This platform fallback is intentional and matches the password flow's AllowPlatformLogin
            // semantics (FindByUserNameAsync/FindByEmailAsync), so a tenant external login can resolve a
            // platform super-admin exactly as a tenant password login can.
            var existing = await userService.FindByEmailAsync(identity.Email, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                try
                {
                    await CreateLinkAsync(existing, provider, identity.Subject, cancellationToken).ConfigureAwait(false);
                    return new ExternalLoginResult(existing, WasCreated: false, WasLinked: true);
                }
                catch (UniqueConstraintException)
                {
                    // A concurrent first-login won the (tenant, provider, subject) filtered-unique index
                    // first. Re-resolve its link and return that user instead of surfacing a 500.
                    return await ResolveExistingLinkAfterRaceAsync(provider, identity.Subject, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        // No match: provision a password-less user and link it, atomically. CreateExternalUserAsync and
        // CreateLinkAsync run inside one transaction so a link-insert failure rolls the new user back —
        // no orphaned user that would dead-end the next login on a duplicate user name.
        try
        {
            return await ProvisionAndLinkAsync(provider, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintException)
        {
            // A concurrent first-login committed the same (tenant, provider, subject) link first; our
            // transaction rolled back. Re-resolve the winner's link and return its user.
            return await ResolveExistingLinkAfterRaceAsync(provider, identity.Subject, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Provisions a password-less user and links it to the external identity inside a single
    /// transaction, so the user and its link commit together (or not at all).</summary>
    private async Task<ExternalLoginResult> ProvisionAndLinkAsync(
        string provider, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        User? user = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Only a provider-verified email is written onto the new user — an unverified (and possibly
            // someone else's) email must neither be claimed nor collide with an existing account, so it
            // is dropped here.
            var provisionEmail = identity.EmailVerified ? identity.Email : null;
            var userName = await DeriveUniqueUserNameAsync(provider, identity, ct).ConfigureAwait(false);
            var created = await userService
                .CreateExternalUserAsync(userName, provisionEmail, identity.EmailVerified, ct)
                .ConfigureAwait(false);
            if (!created.Succeeded || created.UserId is not { } newUserId)
            {
                throw new InvalidOperationException(
                    $"Failed to provision an external user for '{provider}:{identity.Subject}': {created.Error ?? "unknown error"}.");
            }

            user = await IdentityScope.ResolveUserAsync(users, newUserId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Provisioned external user '{newUserId}' does not resolve in scope.");
            await CreateLinkAsync(user, provider, identity.Subject, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new ExternalLoginResult(
            user ?? throw new InvalidOperationException(
                $"Provisioning '{provider}:{identity.Subject}' completed without a user."),
            WasCreated: true,
            WasLinked: true);
    }

    /// <summary>Re-runs the (provider, subject) link lookup after a unique-constraint violation lost the
    /// race, resolving the concurrent winner's user. Returns it as an existing-link result. Throws if
    /// the link still does not resolve (the violation was not the expected race).</summary>
    private async Task<ExternalLoginResult> ResolveExistingLinkAfterRaceAsync(
        string provider, string subject, CancellationToken cancellationToken)
    {
        var link = await links
            .FirstOrDefaultAsync(new ExternalLoginByProviderKeySpec(provider, subject), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"A unique-constraint violation linking '{provider}:{subject}' did not resolve to an existing link.");

        var user = await IdentityScope.ResolveUserAsync(users, link.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"External link '{provider}:{subject}' references user '{link.UserId}', which does not resolve in scope.");
        return new ExternalLoginResult(user, WasCreated: false, WasLinked: false);
    }

    /// <summary>Creates and persists a link from the user to the external (provider, subject), using
    /// the same platform/tenant-aware write path the other Identity services use for child writes.</summary>
    private async Task CreateLinkAsync(User user, string provider, string subject, CancellationToken cancellationToken)
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
        await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, user.TenantId is null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Derives a user name that is unique within the ambient scope: the email local-part when
    /// present (with a short random disambiguator appended on collision), else
    /// <c>{provider}_{subject-prefix}</c>. Probes up to <see cref="MaxDisambiguationAttempts"/> short
    /// suffixes via <see cref="IUserService.FindByUserNameAsync"/>; if all collide it falls back once
    /// to a full UUIDv7 suffix (collision-free in practice) and lets
    /// <see cref="IUserService.CreateExternalUserAsync"/>'s own uniqueness check be authoritative,
    /// so the probe never loops unbounded.</summary>
    private async Task<string> DeriveUniqueUserNameAsync(
        string provider, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var baseName = DeriveBaseUserName(provider, identity);

        var candidate = baseName;
        for (var attempt = 0; attempt < MaxDisambiguationAttempts; attempt++)
        {
            if (await userService.FindByUserNameAsync(candidate, cancellationToken).ConfigureAwait(false) is null)
            {
                return candidate;
            }

            candidate = $"{baseName}_{Guid.NewGuid():N}"[..(baseName.Length + 1 + DisambiguatorLength)];
        }

        // Every short suffix collided: fall back once to a full UUIDv7 suffix. CreateExternalUserAsync
        // re-checks uniqueness, so this is authoritative even against a concurrent insert.
        return $"{baseName}_{Guid.CreateVersion7():N}";
    }

    private static string DeriveBaseUserName(string provider, ExternalIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.Email))
        {
            var atIndex = identity.Email.IndexOf('@', StringComparison.Ordinal);
            var localPart = atIndex > 0 ? identity.Email[..atIndex] : identity.Email;
            if (!string.IsNullOrWhiteSpace(localPart))
            {
                return localPart;
            }
        }

        var subjectPrefix = identity.Subject.Length <= SubjectFallbackLength
            ? identity.Subject
            : identity.Subject[..SubjectFallbackLength];
        return $"{provider}_{subjectPrefix}";
    }
}
