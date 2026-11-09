using System.Globalization;
using System.Security.Claims;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>Puts the stored second-factor proof of a session on a principal as <c>amr</c> and <c>auth_time</c>. The one place that
/// writes either claim, so a token can only carry what the server session holds.</summary>
internal static class SessionProofClaims
{
    internal const string AmrClaim = "amr";
    internal const string AuthTimeClaim = "auth_time";

    /// <summary>Present only while the session is restricted; the value is the <see cref="SessionRestriction"/> name. The gate re-reads the session row before it refuses, so a stale value never locks anyone out.</summary>
    internal const string RestrictionClaim = "sf_restr";

    public static void Apply(ClaimsPrincipal principal, SessionProofSnapshot? proof)
    {
        if (proof is null || principal.Identities.FirstOrDefault() is not { } identity)
        {
            return;
        }

        foreach (var stale in identity.FindAll(AmrClaim).Concat(identity.FindAll(AuthTimeClaim)).Concat(identity.FindAll(RestrictionClaim)).ToList())
        {
            identity.RemoveClaim(stale);
        }

        foreach (var method in proof.Amr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            identity.AddClaim(new Claim(AmrClaim, method));
        }

        if (proof.Restriction != SessionRestriction.None)
        {
            identity.AddClaim(new Claim(RestrictionClaim, proof.Restriction.ToString()));
        }

        identity.AddClaim(new Claim(
            AuthTimeClaim,
            proof.AuthenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64));
    }
}
