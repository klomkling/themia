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
        // FindByIdAsync is tenant-filtered and cannot see a platform user (no tenant, resolved cross-tenant by name),
        // so those are re-read through IPlatformUserReader. A service that lacks it keeps the resolved copy.
        User? fresh;
        if (signedIn.TenantId is null)
        {
            if (users is not IPlatformUserReader platform)
            {
                return signedIn;
            }

            fresh = await platform.FindPlatformUserByIdAsync(signedIn.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            fresh = await users.FindByIdAsync(signedIn.Id, cancellationToken).ConfigureAwait(false);
        }

        if (fresh is null || !fresh.IsActive || fresh.IsLockedOut(timeProvider.GetUtcNow()) || !Equals(fresh.TenantId, signedIn.TenantId))
        {
            return null;
        }

        return fresh;
    }
}
