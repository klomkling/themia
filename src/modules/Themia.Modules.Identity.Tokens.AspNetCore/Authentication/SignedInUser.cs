using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

/// <summary>
/// Re-reads the account after the sign-in success hook ran, so the session is minted from what the store holds now.
/// </summary>
/// <remarks>
/// A hook may change the account (grant a role, confirm a phone), and those mutations rotate the security stamp. A session minted
/// from the copy read before the hook carries the old stamp and fails stamp validation on its first request. The hook may also have
/// deactivated or locked the account, so the same active/lockout gate the sign-in applied is applied again to the fresh copy.
/// </remarks>
internal static class SignedInUser
{
    /// <summary>Returns the account as the store holds it now, or <see langword="null"/> when it may no longer sign in.</summary>
    public static async Task<User?> ReloadAsync(IUserService users, User signedIn, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // A platform user (no tenant) is resolved cross-tenant by name; FindByIdAsync is tenant-filtered, so under
        // a tenant-scoped host it cannot see them and would wrongly report the account gone.
        if (signedIn.TenantId is null)
        {
            return signedIn;
        }

        var fresh = await users.FindByIdAsync(signedIn.Id, cancellationToken).ConfigureAwait(false);
        if (fresh is null || !fresh.IsActive || fresh.IsLockedOut(timeProvider.GetUtcNow()) || !Equals(fresh.TenantId, signedIn.TenantId))
        {
            return null;
        }

        return fresh;
    }
}
