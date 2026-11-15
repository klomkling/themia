using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

/// <summary>The account as the store holds it after a sign-in hook changed it: same id and tenant, a rotated security stamp.</summary>
internal static class ReloadedUser
{
    public static User After(User before, bool isActive = true)
    {
        var after = new User { UserName = before.UserName, TenantId = before.TenantId, IsActive = isActive, SecurityStamp = Guid.NewGuid().ToString("N") };
        after.SetId(before.Id);
        return after;
    }
}
