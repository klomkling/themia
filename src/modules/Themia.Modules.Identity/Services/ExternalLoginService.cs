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
            var existing = await userService.FindByEmailAsync(identity.Email, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await CreateLinkAsync(existing, provider, identity.Subject, cancellationToken).ConfigureAwait(false);
                return new ExternalLoginResult(existing, WasCreated: false, WasLinked: true);
            }
        }

        // No match: provision a password-less user and link it. Only a provider-verified email is
        // written onto the new user — an unverified (and possibly someone else's) email must neither
        // be claimed nor collide with an existing account, so it is dropped here.
        var provisionEmail = identity.EmailVerified ? identity.Email : null;
        var userName = await DeriveUniqueUserNameAsync(provider, identity, cancellationToken).ConfigureAwait(false);
        var created = await userService
            .CreateExternalUserAsync(userName, provisionEmail, identity.EmailVerified, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded || created.UserId is not { } newUserId)
        {
            throw new InvalidOperationException(
                $"Failed to provision an external user for '{provider}:{identity.Subject}': {created.Error ?? "unknown error"}.");
        }

        var user = await IdentityScope.ResolveUserAsync(users, newUserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Provisioned external user '{newUserId}' does not resolve in scope.");
        await CreateLinkAsync(user, provider, identity.Subject, cancellationToken).ConfigureAwait(false);
        return new ExternalLoginResult(user, WasCreated: true, WasLinked: true);
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
    /// <c>{provider}_{subject-prefix}</c>. Loops until <see cref="IUserService.FindByUserNameAsync"/>
    /// reports no collision.</summary>
    private async Task<string> DeriveUniqueUserNameAsync(
        string provider, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var baseName = DeriveBaseUserName(provider, identity);

        var candidate = baseName;
        while (await userService.FindByUserNameAsync(candidate, cancellationToken).ConfigureAwait(false) is not null)
        {
            candidate = $"{baseName}_{Guid.NewGuid():N}"[..(baseName.Length + 1 + DisambiguatorLength)];
        }

        return candidate;
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
