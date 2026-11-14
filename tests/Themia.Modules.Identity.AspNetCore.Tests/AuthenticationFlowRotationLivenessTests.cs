using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

/// <summary>The post-rotation liveness check goes through the member that resolves the owner the way the rotation did, so a refresh
/// from a scope with no tenant still works, and a session that ended is still refused.</summary>
public sealed class AuthenticationFlowRotationLivenessTests
{
    private readonly User user = new() { UserName = "u" };
    private readonly FakeRefreshTokenService refresh = new();
    private readonly InMemorySessionProofStore proofs = new();
    private readonly RefreshIssue successor = new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    private AuthenticationFlow Flow()
    {
        refresh.RotateResult = RefreshValidationResult.Success(user, successor);
        return new AuthenticationFlow(new FakeUserService(), new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(), refresh,
            new FakePasswordHasher(), new RecordingHooks(), [], new IdentityModuleOptions(), TimeProvider.System,
            NullLogger<AuthenticationFlow>.Instance, NotRequiredSecondFactorPolicy.Instance, null, proofs);
    }

    [Fact]
    public async Task A_session_only_the_rotation_scoped_check_sees_still_refreshes()
    {
        var flow = Flow();
        refresh.RotationLiveSessions.Add((user.Id, successor.FamilyId));

        Assert.True((await flow.RefreshAsync("token")).Succeeded);
    }

    [Fact]
    public async Task A_session_that_ended_while_the_token_rotated_is_refused()
    {
        var flow = Flow();

        var result = await flow.RefreshAsync("token");

        Assert.False(result.Succeeded);
    }
}
