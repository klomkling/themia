using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Claim types Themia sets itself when it builds a principal or mints an access token. The claim write APIs
/// refuse them, and the principal factory drops any stored claim of such a type, so a stored row can never shadow or
/// forge an issued claim (the security stamp, the tenant or platform marker, the session id, the roles, the subject).</summary>
public static class ReservedClaimTypes
{
    private const string ThemiaPrefix = "themia:";

    // The long ClaimTypes URIs and their short wire names, plus the registered JWT claims the token descriptor sets.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "sid",
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
        return Reserved.Contains(claimType) || claimType.StartsWith(ThemiaPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
