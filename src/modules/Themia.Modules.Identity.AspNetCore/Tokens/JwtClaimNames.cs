using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Themia.Modules.Identity.AspNetCore.Tokens;

/// <summary>Standard short JWT claim names emitted on the wire for OAuth2/OIDC/gateway interop.
/// The validated server-side principal still carries the long <see cref="ClaimTypes"/> URIs
/// (re-added on validation), so internal consumers (<c>ICurrentUser</c>, <c>[Authorize(Roles)]</c>,
/// the audit accessor) are unaffected.</summary>
internal static class JwtClaimNames
{
    /// <summary>Subject — maps from <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public const string Subject = JwtRegisteredClaimNames.Sub;

    /// <summary>Name — maps from <see cref="ClaimTypes.Name"/>.</summary>
    public const string Name = JwtRegisteredClaimNames.Name;

    /// <summary>Role — maps from <see cref="ClaimTypes.Role"/>. There is no registered "role" claim,
    /// so the conventional ASP.NET short name is used.</summary>
    public const string Role = "role";

    /// <summary>The well-known .NET claim type ↔ standard JWT claim-name pairs that are remapped on
    /// mint (long→short) and validation (short→long). Single source of truth so the two directions
    /// cannot drift.</summary>
    internal static readonly (string Long, string Short)[] WellKnown =
    [
        (ClaimTypes.NameIdentifier, Subject),
        (ClaimTypes.Name, Name),
        (ClaimTypes.Role, Role),
    ];
}
