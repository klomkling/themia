using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Tests;

public class AuthTokenIssuerSessionTests
{
    private sealed class StubFactory : IClaimsPrincipalFactory
    {
        public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], authenticationType)));
    }

    private sealed class CapturingAccessTokens : IAccessTokenService
    {
        public ClaimsPrincipal? Principal { get; private set; }

        public AccessToken Issue(ClaimsPrincipal principal)
        {
            Principal = principal;
            return new AccessToken("jwt", DateTimeOffset.UtcNow.AddMinutes(15));
        }
    }

    private sealed class StubRefreshTokens(Guid familyId) : IRefreshTokenService
    {
        public Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RefreshIssue("raw", DateTimeOffset.UtcNow.AddDays(14), familyId));

        public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task IssueAsync_puts_the_refresh_family_in_the_access_token_as_the_session_id()
    {
        var family = Guid.NewGuid();
        var access = new CapturingAccessTokens();
        var user = new User { UserName = "u" };
        user.SetId(Guid.NewGuid());

        var tokens = await AuthTokenIssuer.IssueAsync(
            new StubFactory(), access, new StubRefreshTokens(family), TimeProvider.System, user, "test", CancellationToken.None);

        Assert.Equal(family.ToString(), access.Principal!.GetSessionId());
        Assert.Equal("jwt", tokens.AccessToken);
        Assert.Equal("raw", tokens.RefreshToken);
    }

    private sealed class FactoryWithStoredSid(string storedSid) : IClaimsPrincipalFactory
    {
        public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", user.Id.ToString()), new Claim(SessionClaims.SessionId, storedSid)], authenticationType)));
    }

    [Fact]
    public async Task IssueAsync_replaces_a_stored_sid_claim_so_it_cannot_shadow_the_real_session()
    {
        var family = Guid.NewGuid();
        var access = new CapturingAccessTokens();
        var user = new User { UserName = "u" };
        user.SetId(Guid.NewGuid());

        await AuthTokenIssuer.IssueAsync(
            new FactoryWithStoredSid(Guid.NewGuid().ToString()), access, new StubRefreshTokens(family), TimeProvider.System, user, "test", CancellationToken.None);

        Assert.Equal([family.ToString()], access.Principal!.FindAll(SessionClaims.SessionId).Select(c => c.Value));
    }
}
