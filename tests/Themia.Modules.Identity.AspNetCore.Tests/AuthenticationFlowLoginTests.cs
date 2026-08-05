using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        Build(PasswordVerificationResult verify, User? user, TimeProvider? clock = null)
    {
        var timeProvider = clock ?? TimeProvider.System;
        var users = new FakeUserService { VerifyResult = verify, UserToReturn = user };
        var refresh = new FakeRefreshTokenService();
        var hasher = new FakePasswordHasher();
        var hooks = new RecordingHooks { Refresh = refresh };
        var flow = new AuthenticationFlow(users, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(timeProvider),
            refresh, hasher, hooks, timeProvider, NullLogger<AuthenticationFlow>.Instance);
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
    [InlineData(PasswordVerificationResult.LockedOut, true)]
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

    [Fact]
    public async Task Login_reports_access_token_lifetime_in_seconds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var (flow, _, _, _, _) = Build(PasswordVerificationResult.Success, NewUser(), clock: clock);
        var result = await flow.LoginAsync("alice", "pw");
        Assert.True(result.Succeeded);
        Assert.Equal(900, result.Tokens!.Value.ExpiresInSeconds); // 15 min = 900 s
    }

    // ---- Multi-identifier resolution (coord #0054) ---------------------------------------------

    private static User NewUser(string userName, Guid? id = null)
    {
        var u = new User { UserName = userName };
        u.SetId(id ?? Guid.NewGuid());
        return u;
    }

    [Fact]
    public async Task Login_resolves_a_confirmed_email_to_its_user()
    {
        var alice = NewUser("alice");
        var (flow, users, _, _, _) = Build(PasswordVerificationResult.Success, null);
        users.UserToReturn = null;                                   // not a username
        users.EmailUserToReturn = alice;
        alice.EmailConfirmed = true;

        var result = await flow.LoginAsync("alice@example.com", "pw");

        Assert.True(result.Succeeded);
        // Lockout and verification are keyed on the USERNAME, never on what the caller typed —
        // otherwise each identifier would carry its own independent attempt budget.
        Assert.Equal("alice", users.VerifiedUserName);
    }

    [Fact]
    public async Task Login_resolves_a_confirmed_phone_to_its_user()
    {
        var alice = NewUser("alice");
        alice.PhoneNumberConfirmed = true;
        var (flow, users, _, _, _) = Build(PasswordVerificationResult.Success, null);
        users.PhoneUserToReturn = alice;

        var result = await flow.LoginAsync("+66811112222", "pw");

        Assert.True(result.Succeeded);
        Assert.Equal("alice", users.VerifiedUserName);
    }

    [Theory]
    [InlineData(false, true)]   // email found but unconfirmed
    [InlineData(true, false)]   // phone found but unconfirmed
    public async Task Login_refuses_an_unconfirmed_email_or_phone(bool phoneCase, bool emailCase)
    {
        var alice = NewUser("alice");
        alice.EmailConfirmed = false;
        alice.PhoneNumberConfirmed = false;
        var (flow, users, _, hasher, hooks) = Build(PasswordVerificationResult.Success, null);
        users.EmailUserToReturn = emailCase ? alice : null;
        users.PhoneUserToReturn = phoneCase ? alice : null;

        var result = await flow.LoginAsync("alice@example.com", "pw");

        // An unconfirmed identifier is a claim, not proof of control. Anyone who can type someone
        // else's address into their own profile would otherwise be able to log in as them.
        Assert.False(result.Succeeded);
        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(LoginFailureReason.NotFound, hooks.FailedReason);
        Assert.Equal(1, hasher.HashCalls);
    }

    [Fact]
    public async Task Login_refuses_an_identifier_that_matches_two_different_users()
    {
        // A's username is B's email. Per-column uniqueness cannot prevent this, and resolving it to
        // either user silently hands one account to whoever knows the other's password.
        var a = NewUser("shared@example.com");
        var b = NewUser("bob");
        b.EmailConfirmed = true;

        var (flow, users, _, hasher, hooks) = Build(PasswordVerificationResult.Success, null);
        users.UserToReturn = a;
        users.EmailUserToReturn = b;

        var result = await flow.LoginAsync("shared@example.com", "pw");

        Assert.False(result.Succeeded);
        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);      // indistinguishable outward
        Assert.Equal(LoginFailureReason.AmbiguousIdentifier, hooks.FailedReason);  // visible inward
        Assert.Equal(0, users.VerifyCalls);                                 // no password was checked
        Assert.Equal(1, hasher.HashCalls);                                  // but the timing was equalized
    }

    [Fact]
    public async Task Login_succeeds_when_one_user_matches_on_two_columns()
    {
        // A user whose username IS their email matches twice — same id, so not a collision.
        var alice = NewUser("alice@example.com");
        alice.EmailConfirmed = true;
        var (flow, users, _, _, _) = Build(PasswordVerificationResult.Success, null);
        users.UserToReturn = alice;
        users.EmailUserToReturn = alice;

        var result = await flow.LoginAsync("alice@example.com", "pw");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Login_prefers_the_username_match_when_one_user_holds_several_identifiers()
    {
        var alice = NewUser("alice");
        alice.EmailConfirmed = true;
        var (flow, users, _, _, _) = Build(PasswordVerificationResult.Success, null);
        users.UserToReturn = alice;
        users.EmailUserToReturn = alice;

        Assert.True((await flow.LoginAsync("alice", "pw")).Succeeded);
        Assert.Equal("alice", users.VerifiedUserName);
    }

    [Fact]
    public async Task Login_hashes_once_even_when_no_identifier_matches()
    {
        // Resolution failing is an early exit that did not exist when the identifier was always a
        // username. Without the compensating hash, three lookups and no argon2 would be measurably
        // faster than a wrong password — a timing oracle across three identifier spaces.
        var (flow, _, _, hasher, _) = Build(PasswordVerificationResult.Success, null);

        var result = await flow.LoginAsync("nobody@example.com", "pw");

        Assert.False(result.Succeeded);
        Assert.Equal(1, hasher.HashCalls);
    }

    [Fact]
    public async Task Login_queries_all_three_identifier_columns_even_when_the_username_matches()
    {
        // Short-circuiting on the first hit would (a) miss collisions entirely and (b) make the number
        // of round trips reveal which identifier space a string belongs to.
        var alice = NewUser("alice");
        var (flow, users, _, _, _) = Build(PasswordVerificationResult.Success, null);
        users.UserToReturn = alice;

        await flow.LoginAsync("alice", "pw");

        Assert.Equal(1, users.FindByUserNameCalls);
        Assert.Equal(1, users.FindByEmailCalls);
        Assert.Equal(1, users.FindByPhoneCalls);
    }
}
