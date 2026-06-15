using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AuthenticationFlowLoginTests
{
    private static User NewUser() => new() { UserName = "alice" };

    private static (AuthenticationFlow Flow, FakeUserService Users, FakeRefreshTokenService Refresh, FakePasswordHasher Hasher, RecordingHooks Hooks)
        Build(PasswordVerificationResult verify, User? user)
    {
        var users = new FakeUserService { VerifyResult = verify, UserToReturn = user };
        var refresh = new FakeRefreshTokenService();
        var hasher = new FakePasswordHasher();
        var hooks = new RecordingHooks { Refresh = refresh };
        var flow = new AuthenticationFlow(users, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(),
            refresh, hasher, hooks, TimeProvider.System);
        return (flow, users, refresh, hasher, hooks);
    }

    [Fact]
    public async Task Login_succeeds_and_issues_a_pair()
    {
        var (flow, _, refresh, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        var result = await flow.LoginAsync("alice", "pw");
        Assert.True(result.Succeeded);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("refresh-raw", result.Tokens!.Value.RefreshToken);
        Assert.Equal(1, refresh.IssueCalls);
        Assert.True(hooks.SucceededRanBeforeIssue);
    }

    [Theory]
    [InlineData(PasswordVerificationResult.NotFound, LoginFailureReason.NotFound)]
    [InlineData(PasswordVerificationResult.Failed, LoginFailureReason.WrongPassword)]
    [InlineData(PasswordVerificationResult.Inactive, LoginFailureReason.Inactive)]
    [InlineData(PasswordVerificationResult.LockedOut, LoginFailureReason.LockedOut)]
    public async Task Login_failures_do_not_issue_tokens_and_report_real_reason(PasswordVerificationResult verify, LoginFailureReason reason)
    {
        var (flow, _, refresh, _, hooks) = Build(verify, NewUser());
        var result = await flow.LoginAsync("alice", "pw");
        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(reason, hooks.FailedReason);
    }

    [Theory]
    [InlineData(PasswordVerificationResult.NotFound, true)]
    [InlineData(PasswordVerificationResult.Inactive, true)]
    [InlineData(PasswordVerificationResult.Failed, false)]
    public async Task Login_runs_throwaway_hash_only_when_no_real_hash_ran(PasswordVerificationResult verify, bool expectBurn)
    {
        var (flow, _, _, hasher, _) = Build(verify, NewUser());
        await flow.LoginAsync("alice", "pw");
        Assert.Equal(expectBurn ? 1 : 0, hasher.HashCalls);
    }

    [Fact]
    public async Task Login_denied_by_before_hook_returns_denied_and_fires_failed_hook()
    {
        var (flow, users, _, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyBeforeLogin = true;
        var result = await flow.LoginAsync("alice", "pw");
        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, users.VerifyCalls);
        Assert.Equal(LoginFailureReason.Denied, hooks.FailedReason);
    }

    [Fact]
    public async Task Login_denied_by_succeeded_hook_returns_denied_without_issuing()
    {
        var (flow, _, refresh, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyOnSucceeded = true;
        var result = await flow.LoginAsync("alice", "pw");
        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(LoginFailureReason.Denied, hooks.FailedReason);
    }
}
