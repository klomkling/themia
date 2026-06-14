using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Specifications;

/// <summary>Finds a user by normalized user name within the ambient tenant.</summary>
internal sealed class UserByNormalizedNameSpec : Specification<User>
{
    public UserByNormalizedNameSpec(string normalizedUserName) =>
        Where(u => u.NormalizedUserName == normalizedUserName);
}

/// <summary>Finds a platform (global) user by normalized user name, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedNameSpec : Specification<User>
{
    public PlatformUserByNormalizedNameSpec(string normalizedUserName)
    {
        Where(u => u.NormalizedUserName == normalizedUserName && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a user by normalized email within the ambient tenant.</summary>
internal sealed class UserByNormalizedEmailSpec : Specification<User>
{
    public UserByNormalizedEmailSpec(string normalizedEmail) =>
        Where(u => u.NormalizedEmail == normalizedEmail);
}

/// <summary>Finds a platform (global) user by normalized email, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedEmailSpec : Specification<User>
{
    public PlatformUserByNormalizedEmailSpec(string normalizedEmail)
    {
        Where(u => u.NormalizedEmail == normalizedEmail && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a role by normalized name within the ambient tenant.</summary>
internal sealed class RoleByNormalizedNameSpec : Specification<Role>
{
    public RoleByNormalizedNameSpec(string normalizedName) =>
        Where(r => r.NormalizedName == normalizedName);
}

/// <summary>Finds a platform (global) role by normalized name, bypassing the tenant filter.</summary>
internal sealed class PlatformRoleByNormalizedNameSpec : Specification<Role>
{
    public PlatformRoleByNormalizedNameSpec(string normalizedName)
    {
        Where(r => r.NormalizedName == normalizedName && r.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>All membership rows for a user.</summary>
internal sealed class UserRolesByUserSpec : Specification<UserRole>
{
    public UserRolesByUserSpec(Guid userId) => Where(ur => ur.UserId == userId);
}

/// <summary>A specific user–role membership row.</summary>
internal sealed class UserRoleSpec : Specification<UserRole>
{
    public UserRoleSpec(Guid userId, Guid roleId) => Where(ur => ur.UserId == userId && ur.RoleId == roleId);
}
