using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AuthenticationFlowRefreshTests
{
    private static AuthenticationFlow Build(FakeRefreshTokenService refresh, RecordingHooks hooks) =>
        new(new FakeUserService(), new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(),
            refresh, new FakePasswordHasher(), hooks, TimeProvider.System, NullLogger<AuthenticationFlow>.Instance);

    private static RefreshIssue Successor() => new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    [Fact]
    public async Task Refresh_success_mints_a_new_pair()
    {
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(new User { UserName = "u" }, Successor()),
        };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.True(result.Succeeded);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("new-refresh", result.Tokens!.Value.RefreshToken);
    }

    [Fact]
    public async Task Refresh_rejected_when_user_is_inactive()
    {
        // A deactivated account must not keep minting tokens via refresh even with a valid refresh token.
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(new User { UserName = "u", IsActive = false }, Successor()),
        };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Refresh_rejected_when_user_is_locked_out()
    {
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(
                new User { UserName = "u", LockoutEnabled = true, LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10) },
                Successor()),
        };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Refresh_invalid_returns_invalid()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.Invalid() };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Refresh_reuse_returns_reuse_detected()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.ReuseDetected() };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.ReuseDetected, result.Outcome);
    }

    [Fact]
    public async Task Refresh_denied_by_before_hook_does_not_rotate()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.Invalid() };
        var hooks = new RecordingHooks { DenyBeforeRefresh = true };
        var result = await Build(refresh, hooks).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task Refresh_denied_by_succeeded_hook_returns_denied()
    {
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(new User { UserName = "u" }, Successor()),
        };
        var hooks = new RecordingHooks { DenyRefreshSucceeded = true };
        var result = await Build(refresh, hooks).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task Logout_revokes_single_family_by_default()
    {
        var refresh = new FakeRefreshTokenService();
        await Build(refresh, new RecordingHooks()).LogoutAsync("token", allSessions: false);
        Assert.Equal(1, refresh.RevokeCalls);
        Assert.False(refresh.LastRevokeAllForUser);
    }

    [Fact]
    public async Task Logout_all_revokes_every_session()
    {
        var refresh = new FakeRefreshTokenService();
        await Build(refresh, new RecordingHooks()).LogoutAsync("token", allSessions: true);
        Assert.True(refresh.LastRevokeAllForUser);
    }
}
