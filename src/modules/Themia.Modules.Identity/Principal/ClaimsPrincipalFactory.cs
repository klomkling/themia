using System.Security.Claims;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Principal;

/// <summary>Default <see cref="IClaimsPrincipalFactory"/>. The single source of truth for principal contents.</summary>
public sealed class ClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IRepository<Role, Guid> roles;
    private readonly IClaimService claims;

    /// <summary>Creates the factory.</summary>
    public ClaimsPrincipalFactory(
        IRepository<UserRole, Guid> memberships,
        IRepository<Role, Guid> roles,
        IClaimService claims)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(claims);
        this.memberships = memberships;
        this.roles = roles;
        this.claims = claims;
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationType);

        // ClaimTypes.Role + ClaimTypes.NameIdentifier so [Authorize(Roles=...)] and User identity work out of the box.
        var identity = new ClaimsIdentity(authenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
        identity.AddClaim(new Claim(IdentityClaimTypes.SecurityStamp, user.SecurityStamp));

        if (user.TenantId is { } tenant)
        {
            identity.AddClaim(new Claim(IdentityClaimTypes.TenantId, tenant.Value));
        }

        var roleIds = (await memberships.ListAsync(new UserRolesByUserSpec(user.Id), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();
        if (roleIds.Count > 0)
        {
            var roleRows = await roles.ListAsync(new RolesByIdsSpec(roleIds), cancellationToken).ConfigureAwait(false);
            foreach (var role in roleRows)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
            }
        }

        foreach (var claim in await claims.GetEffectiveClaimsAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            identity.AddClaim(claim);
        }

        return new ClaimsPrincipal(identity);
    }
}
