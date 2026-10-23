using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Xunit;

namespace Themia.Modules.Identity.Tests;

public class SessionClaimsTests
{
    private sealed class StubCurrentUser(params Claim[] claims) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => null;
        public string? TenantId => null;
        public bool IsPlatform => false;
        public string? UserName => null;
        public IReadOnlyCollection<string> Roles => [];
        public IReadOnlyCollection<Claim> Claims { get; } = claims;
        public bool IsInRole(string role) => false;
    }

    [Fact]
    public void GetSessionId_reads_the_sid_claim_from_the_current_user()
    {
        var user = new StubCurrentUser(new Claim("sub", "u"), new Claim(SessionClaims.SessionId, "abc"));
        Assert.Equal("abc", user.GetSessionId());
    }

    [Fact]
    public void GetSessionId_is_null_when_the_token_has_no_sid()
    {
        Assert.Null(new StubCurrentUser(new Claim("sub", "u")).GetSessionId());
    }

    [Fact]
    public void GetSessionId_reads_the_sid_claim_from_a_principal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionClaims.SessionId, "xyz")], "t"));
        Assert.Equal("xyz", principal.GetSessionId());
    }

    [Fact]
    public void The_claim_type_is_the_standard_sid()
    {
        Assert.Equal("sid", SessionClaims.SessionId);
    }
}
