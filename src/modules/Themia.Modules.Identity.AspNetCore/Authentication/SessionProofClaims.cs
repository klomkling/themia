using System.Globalization;
using System.Security.Claims;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>Puts the stored second-factor proof of a session on a principal as <c>amr</c> and <c>auth_time</c>. The one place that
/// writes either claim, so a token can only carry what the server session holds.</summary>
internal static class SessionProofClaims
{
    internal const string AmrClaim = "amr";
    internal const string AuthTimeClaim = "auth_time";

    public static void Apply(ClaimsPrincipal principal, SessionProofSnapshot? proof)
    {
        if (proof is null || principal.Identities.FirstOrDefault() is not { } identity)
        {
            return;
        }

        foreach (var stale in identity.FindAll(AmrClaim).Concat(identity.FindAll(AuthTimeClaim)).ToList())
        {
            identity.RemoveClaim(stale);
        }

        foreach (var method in proof.Amr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            identity.AddClaim(new Claim(AmrClaim, method));
        }

        identity.AddClaim(new Claim(
            AuthTimeClaim,
            proof.AuthenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64));
    }
}
