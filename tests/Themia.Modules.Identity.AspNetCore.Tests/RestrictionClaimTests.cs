using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class RestrictionClaimTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    private static SessionProofSnapshot Proof(SessionRestriction restriction) =>
        new("otp", SessionProofStrength.Weak, DateTimeOffset.UnixEpoch, restriction);

    [Theory]
    [InlineData(SessionRestriction.RecoveryOnly, "RecoveryOnly")]
    [InlineData(SessionRestriction.UpgradeDue, "UpgradeDue")]
    public void Apply_ShouldWriteTheRestriction_WhenTheSessionIsRestricted(SessionRestriction restriction, string expected)
    {
        var principal = Principal();

        SessionProofClaims.Apply(principal, Proof(restriction));

        Assert.Equal(expected, principal.FindFirst("sf_restr")?.Value);
    }

    [Fact]
    public void Apply_ShouldWriteNoClaim_WhenThereIsNoRestriction()
    {
        var principal = Principal();

        SessionProofClaims.Apply(principal, Proof(SessionRestriction.None));

        Assert.Null(principal.FindFirst("sf_restr"));
    }

    [Fact]
    public void Apply_ShouldStripAStaleRestriction_WhenTheSessionIsNoLongerRestricted()
    {
        var principal = Principal(new Claim("sf_restr", "RecoveryOnly"));

        SessionProofClaims.Apply(principal, Proof(SessionRestriction.None));

        Assert.Null(principal.FindFirst("sf_restr"));
    }

    [Fact]
    public void Apply_ShouldKeepExactlyOneRestrictionClaim()
    {
        var principal = Principal(new Claim("sf_restr", "RecoveryOnly"));

        SessionProofClaims.Apply(principal, Proof(SessionRestriction.UpgradeDue));

        Assert.Equal("UpgradeDue", Assert.Single(principal.FindAll("sf_restr")).Value);
    }

    [Theory]
    [InlineData("sf_restr")]
    [InlineData("SF_RESTR")]
    public void IsReserved_ShouldBeTrue_ForTheRestrictionClaim(string type) =>
        Assert.True(ReservedClaimTypes.IsReserved(type));
}
