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
