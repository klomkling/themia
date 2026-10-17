using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>
/// Default <see cref="IOtpRecipientResolver"/>. Reads the users holding a phone number without the tenant
/// filter (a start request has no JWT, so under a claims-only strategy the ambient scope is null), keeps only
/// the ones that may log in, and answers only when exactly one is left. It hands back identifiers, never a
/// <see cref="User"/>, and the number is not looked up by anything the caller chooses beyond the phone itself.
/// </summary>
public sealed class OtpRecipientResolver : IOtpRecipientResolver
{
    private readonly IReadRepository<User, Guid> users;
    private readonly ITenantContext tenantContext;
    private readonly IdentityModuleOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the resolver.</summary>
    public OtpRecipientResolver(IReadRepository<User, Guid> users, ITenantContext tenantContext, IdentityModuleOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.users = users;
        this.options = options;
        this.tenantContext = tenantContext;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<OtpRecipient?> ResolveAsync(string normalizedPhoneNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPhoneNumber);

        var holders = await users.ListAsync(new ActiveUsersByNormalizedPhoneAnyTenantSpec(normalizedPhoneNumber), cancellationToken).ConfigureAwait(false);

        // With an ambient tenant (a host that does send a hint) only that tenant and the platform are
        // candidates; with none, every tenant is, and a number shared between tenants is ambiguous.
        var ambient = tenantContext.CurrentTenantId;
        var now = timeProvider.GetUtcNow();
        var eligible = holders
            // The same gate IUserService.FindBy* applies: a platform user is only a candidate when platform login is on.
            .Where(u => u.TenantId is not null || options.AllowPlatformLogin)
            .Where(u => ambient is null || u.TenantId is null || Equals(u.TenantId, ambient))
            .Where(u => u.PhoneNumberConfirmed && !u.IsLockedOut(now))
            .Take(2)
            .ToList();

        return eligible.Count == 1
            ? new OtpRecipient(eligible[0].Id, eligible[0].TenantId, eligible[0].PhoneNumber ?? normalizedPhoneNumber)
            : null;
    }
}
