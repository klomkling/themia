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
    private const int MaxRaceRetries = 3;

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<ExternalLoginLink, Guid> links;
    private readonly IUserService userService;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IDataFilterScope filterScope;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The user repository.</param>
    /// <param name="links">The external-login-link repository.</param>
    /// <param name="userService">The user service used to provision password-less users.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="filterScope">The data filter scope used for platform-aware writes.</param>
    /// <param name="options">The Identity module options (for the platform-login fallback).</param>
    public ExternalLoginService(
        IRepository<User, Guid> users,
        IRepository<ExternalLoginLink, Guid> links,
        IUserService userService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IDataFilterScope filterScope,
        IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(filterScope);
        ArgumentNullException.ThrowIfNull(options);
        this.users = users;
        this.links = links;
        this.userService = userService;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.filterScope = filterScope;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginResult> ResolveOrProvisionAsync(
        ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Subject);

        var provider = identity.Provider.ToLowerInvariant();

        // A concurrent first-login can win a race on the (tenant, provider, subject) link index OR on the
        // new user's unique name/email index. All of these surface as a UniqueConstraintException (a
        // duplicate name/email caught by the pre-insert probe is funnelled into the same signal below);
        // on retry the winner's rows are visible, so the next pass resolves the existing link, auto-links
        // by verified email, or derives a fresh user name instead of dead-ending on a 500. Bounded so a
        // pathological, sustained conflict surfaces rather than spinning.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ResolveOrProvisionCoreAsync(provider, identity, cancellationToken).ConfigureAwait(false);
            }
            catch (UniqueConstraintException) when (attempt < MaxRaceRetries)
            {
                // Retry: re-read the now-committed winner.
            }
        }
    }

    /// <summary>One resolve/auto-link/provision pass. A lost race surfaces as a
    /// <see cref="UniqueConstraintException"/> for the caller's bounded retry to absorb.</summary>
    private async Task<ExternalLoginResult> ResolveOrProvisionCoreAsync(
        string provider, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        // Existing link (tenant-scoped, then the platform fallback when AllowPlatformLogin is set —
        // mirroring IUserService.FindByEmailAsync).
        if (await ResolveExistingLinkAsync(provider, identity.Subject, cancellationToken).ConfigureAwait(false) is { } existingLink)
        {
            return existingLink;
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
                // Never bind a new external credential to a deactivated or locked account: return it
                // un-linked so the flow's active/lockout gate blocks the login. Otherwise a later
                // re-activation would silently inherit a usable external login the admin never approved.
                if (!existing.IsActive || existing.IsLockedOut(timeProvider.GetUtcNow()))
                {
                    return new ExternalLoginResult(existing, WasCreated: false, WasLinked: false);
                }

                // Wrap in a transaction so a lost link-insert race rolls back and (on EF) clears the
                // change tracker, keeping the caller's retry clean.
                await unitOfWork.ExecuteInTransactionAsync(
                    ct => CreateLinkAsync(existing, provider, identity.Subject, ct), cancellationToken).ConfigureAwait(false);
                return new ExternalLoginResult(existing, WasCreated: false, WasLinked: true);
            }
        }

        // No match: provision a password-less user and link it, atomically. CreateExternalUserAsync and
        // CreateLinkAsync run inside one transaction so a link-insert failure rolls the new user back —
        // no orphaned user that would dead-end the next login on a duplicate user name.
        return await ProvisionAndLinkAsync(provider, identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves an existing (provider, subject) link to its user: the tenant-scoped link first,
    /// then — when <see cref="IdentityModuleOptions.AllowPlatformLogin"/> is set — the platform (global)
    /// link. Returns <see langword="null"/> when no link exists. The platform fallback matters on a data
    /// layer that does not surface global (<c>tenant_id IS NULL</c>) rows to a tenant scope (e.g. Dapper
    /// with the default <c>IncludeGlobalRecordsForTenants=false</c>): without it, a platform user's second
    /// external login would re-insert the link and hit the platform unique index.</summary>
    private async Task<ExternalLoginResult?> ResolveExistingLinkAsync(
        string provider, string subject, CancellationToken cancellationToken)
    {
        var link = await links
            .FirstOrDefaultAsync(new ExternalLoginByProviderKeySpec(provider, subject), cancellationToken)
            .ConfigureAwait(false);
        if (link is not null)
        {
            var user = await IdentityScope.ResolveUserAsync(users, link.UserId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"External link '{provider}:{subject}' references user '{link.UserId}', which does not resolve in scope.");
            return new ExternalLoginResult(user, WasCreated: false, WasLinked: false);
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        var platformLink = await links
            .FirstOrDefaultAsync(new PlatformExternalLoginByProviderKeySpec(provider, subject), cancellationToken)
            .ConfigureAwait(false);
        if (platformLink is null)
        {
            return null;
        }

        var platformUser = await users
            .FirstOrDefaultAsync(new PlatformUserByIdSpec(platformLink.UserId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Platform external link '{provider}:{subject}' references user '{platformLink.UserId}', which does not resolve.");
        return new ExternalLoginResult(platformUser, WasCreated: false, WasLinked: false);
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
                // A concurrent first-login took this user name or verified email between our uniqueness
                // probe and insert. Funnel into the same race-retry signal as a unique-index violation, so
                // the next pass auto-links by email or derives a fresh name instead of failing the login.
                if (created.Error is "duplicate_user_name" or "duplicate_email")
                {
                    throw new UniqueConstraintException(
                        $"Provisioning '{provider}:{identity.Subject}' lost a race on {created.Error}.");
                }

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
