using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Xunit;

namespace Themia.Modules.Identity.Tests;

public class ReservedClaimTypesTests
{
    [Theory]
    [InlineData("sid")]
    [InlineData("themia:security_stamp")]
    [InlineData("themia:is_platform")]
    [InlineData("themia:tenant_id")]
    [InlineData("themia:anything_added_later")]
    [InlineData("THEMIA:Is_Platform")]
    [InlineData(ClaimTypes.Role)]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData(ClaimTypes.Name)]
    [InlineData("sub")]
    [InlineData("name")]
    [InlineData("role")]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("iat")]
    [InlineData("jti")]
    public void IsReserved_is_true_for_a_claim_type_Themia_sets(string claimType) =>
        Assert.True(ReservedClaimTypes.IsReserved(claimType));

    [Theory]
    [InlineData("perm")]
    [InlineData("perm:invoice.write")]
    [InlineData(ClaimTypes.Email)]
    [InlineData("department")]
    [InlineData("themiax")]
    public void IsReserved_is_false_for_an_app_claim_type(string claimType) =>
        Assert.False(ReservedClaimTypes.IsReserved(claimType));
}
