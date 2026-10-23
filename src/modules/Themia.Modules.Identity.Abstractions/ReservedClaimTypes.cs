using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Claim types Themia sets itself when it builds a principal or mints an access token. The claim write APIs
/// refuse them, and the principal factory drops any stored claim of such a type, so a stored row can never shadow or
/// forge an issued claim (the security stamp, the tenant or platform marker, the session id, the roles, the subject).</summary>
/// <remarks>The list is fixed. It covers <c>tenant_id</c>, the default claim type of the claims tenant-resolution
/// strategy, but not a custom <c>MultiTenancyOptions.ClaimType</c>: an app that configures its own tenant claim type
/// must not store that type as a user or role claim, or the stored value would reach the principal and be trusted.</remarks>
public static class ReservedClaimTypes
{
    private const string ThemiaPrefix = "themia:";

    // Case-insensitive because ClaimsIdentity.FindFirst and IsInRole compare claim types that way, so 'ROLE' or an
    // upper-cased ClaimTypes.Role would otherwise pass the check and still act as the real claim.
    // The long ClaimTypes URIs and their short wire names, plus the registered JWT claims the token descriptor sets.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "sid",
        "tenant_id",   // the claims tenant-resolution strategy reads this by default (MultiTenancyOptions.ClaimType)
        ClaimTypes.NameIdentifier,
        ClaimTypes.Name,
        ClaimTypes.Role,
        "sub",
        "name",
        "role",
        "iss",
        "aud",
        "exp",
        "nbf",
        "iat",
        "jti",
    };

    /// <summary>Whether Themia sets this claim type itself: <c>sid</c>, every <c>themia:*</c> claim, the role, name and
    /// subject claims (long and short forms) and the registered JWT claims.</summary>
    /// <param name="claimType">The claim type.</param>
    public static bool IsReserved(string claimType)
    {
        ArgumentNullException.ThrowIfNull(claimType);
        var type = claimType.Trim();
        return Reserved.Contains(type) || type.StartsWith(ThemiaPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
