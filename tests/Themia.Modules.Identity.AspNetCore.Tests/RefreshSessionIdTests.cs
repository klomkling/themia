using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class RefreshSessionIdTests
{
    private sealed class CapturingAccessTokens : IAccessTokenService
    {
        public ClaimsPrincipal? Principal { get; private set; }

        public AccessToken Issue(ClaimsPrincipal principal)
        {
            Principal = principal;
            return new AccessToken("access-jwt", DateTimeOffset.UtcNow.AddMinutes(15));
        }
    }

    [Fact]
    public async Task Refresh_keeps_the_session_id_of_the_rotated_family()
    {
        var family = Guid.NewGuid();
        var access = new CapturingAccessTokens();
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(
                new User { UserName = "u" }, new RefreshIssue("new-refresh", DateTimeOffset.UtcNow.AddDays(14), family)),
        };
        var flow = new AuthenticationFlow(
            new FakeUserService(), new FakeClaimsPrincipalFactory(), access, refresh, new FakePasswordHasher(),
            new RecordingHooks(), TimeProvider.System, NullLogger<AuthenticationFlow>.Instance);

        var result = await flow.RefreshAsync("token");

        Assert.True(result.Succeeded);
        Assert.Equal(family.ToString(), access.Principal!.GetSessionId());
    }
}
