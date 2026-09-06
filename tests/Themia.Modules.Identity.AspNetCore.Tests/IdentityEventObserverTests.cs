using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

/// <summary>Coverage for <see cref="IIdentityEventObserver"/> fan-out across every
/// <see cref="AuthenticationFlow"/> path: password login, refresh (all five outcomes), and logout.
/// External login and user-mutation/lockout coverage live in their own projects
/// (Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests and Themia.Modules.Identity.Tests) since
/// <see cref="AuthenticationFlow"/> does not own those flows.</summary>
public sealed class IdentityEventObserverTests
{
    private static User NewUser(string userName = "alice") => new() { UserName = userName };

    private static (AuthenticationFlow Flow, FakeUserService Users, FakeRefreshTokenService Refresh, RecordingHooks Hooks, RecordingObserver Observer)
        Build(PasswordVerificationResult verify, User? user, params IIdentityEventObserver[] observers)
    {
        var refresh = new FakeRefreshTokenService();
        var users = new FakeUserService { VerifyResult = verify, UserToReturn = user };
        var hooks = new RecordingHooks { Refresh = refresh };
        var recorder = new RecordingObserver();
        var allObservers = observers.Length == 0 ? [recorder] : observers.Append(recorder).ToArray();
        var flow = new AuthenticationFlow(
            users, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(), refresh, new FakePasswordHasher(),
            hooks, allObservers, TimeProvider.System, NullLogger<AuthenticationFlow>.Instance);
        return (flow, users, refresh, hooks, recorder);
    }

    private static RefreshIssue Successor() => new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    // ── Password login ──────────────────────────────────────────────────────

    [Fact]
    public async Task Login_success_raises_OnLoginSucceeded()
    {
        var (flow, _, _, _, observer) = Build(PasswordVerificationResult.Success, NewUser());
        await flow.LoginAsync("alice", "pw");
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginSucceededAsync), observer.Calls);
    }

    [Fact]
    public async Task Login_wrong_password_raises_OnLoginFailed()
    {
        var (flow, _, _, _, observer) = Build(PasswordVerificationResult.Failed, NewUser());
        await flow.LoginAsync("alice", "bad");
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginFailedAsync), observer.Calls);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnLoginDeniedAsync), observer.Calls);
    }

    [Fact]
    public async Task Login_denied_by_before_hook_raises_OnLoginDenied_not_OnLoginFailed()
    {
        var (flow, _, _, hooks, observer) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyBeforeLogin = true;
        await flow.LoginAsync("alice", "pw");
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginDeniedAsync), observer.Calls);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnLoginFailedAsync), observer.Calls);
    }

    [Fact]
    public async Task Login_denied_by_succeeded_hook_raises_OnLoginDenied()
    {
        var (flow, _, _, hooks, observer) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyOnSucceeded = true;
        await flow.LoginAsync("alice", "pw");
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginDeniedAsync), observer.Calls);
    }

    [Fact]
    public async Task Observers_and_the_adopters_own_login_hooks_both_run()
    {
        var (flow, _, _, hooks, observer) = Build(PasswordVerificationResult.Success, NewUser());
        await flow.LoginAsync("alice", "pw");
        Assert.Contains("login-succeeded", hooks.Calls);
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginSucceededAsync), observer.Calls);
    }

    // ── Refresh: all five outcomes ───────────────────────────────────────────

    [Fact]
    public async Task Refresh_success_raises_OnRefreshSucceeded()
    {
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshSucceededAsync), observer.Calls);
    }

    [Fact]
    public async Task Refresh_reuse_detected_raises_OnRefreshFailed()
    {
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshFailedAsync), observer.Calls);
        Assert.Equal(RefreshOutcome.ReuseDetected, observer.LastRefreshFailed!.Value.Outcome);
    }

    [Fact]
    public async Task Refresh_reuse_detected_attributes_the_event_to_the_owning_account()
    {
        // ReuseDetected is the single most actionable event in this flow — an operator needs the account,
        // not "some token was replayed", to revoke sessions right now.
        var ownerId = Guid.NewGuid();
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        refresh.OwnerId = ownerId;

        await flow.RefreshAsync("token");

        Assert.Equal(1, refresh.ResolveOwnerCalls);
        Assert.Equal(ownerId, observer.LastRefreshFailed!.Value.UserId);
    }

    [Fact]
    public async Task Refresh_invalid_raises_OnRefreshFailed()
    {
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Invalid();
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshFailedAsync), observer.Calls);
        Assert.Equal(RefreshOutcome.Invalid, observer.LastRefreshFailed!.Value.Outcome);
    }

    [Fact]
    public async Task Refresh_reuse_detected_survives_a_failed_owner_lookup()
    {
        // Losing attribution is acceptable; turning a refresh rejection into a 500 is not.
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        refresh.ThrowOnResolveOwner = true;

        var result = await flow.RefreshAsync("token");

        Assert.Equal(RefreshRotationOutcome.ReuseDetected, result.Outcome);
        Assert.Null(observer.LastRefreshFailed!.Value.UserId);
    }

    [Fact]
    public async Task Refresh_invalid_passes_a_null_owner_and_never_looks_one_up()
    {
        // Guards against someone later "simplifying" the two paths into one: Invalid has no owner by
        // construction, and resolving one here would run a pointless lookup on every bad token an
        // attacker sprays at the endpoint.
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Invalid();
        refresh.OwnerId = Guid.NewGuid(); // even if a lookup WOULD resolve something, it must not run

        await flow.RefreshAsync("token");

        Assert.Equal(0, refresh.ResolveOwnerCalls);
        Assert.Null(observer.LastRefreshFailed!.Value.UserId);
    }

    [Fact]
    public async Task Refresh_inactive_account_raises_OnRefreshFailed_with_userId()
    {
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "alice", IsActive = false }, Successor());
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshFailedAsync), observer.Calls);
        Assert.NotNull(observer.LastRefreshFailed!.Value.UserId);
    }

    [Fact]
    public async Task Refresh_denied_before_rotation_reports_rotationCommitted_false()
    {
        var (flow, _, refresh, hooks, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Invalid();
        hooks.DenyBeforeRefresh = true;
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshDeniedAsync), observer.Calls);
        Assert.False(observer.LastRefreshDenied!.Value.RotationCommitted);
    }

    [Fact]
    public async Task Refresh_denied_after_rotation_reports_rotationCommitted_true()
    {
        var (flow, _, refresh, hooks, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());
        hooks.DenyRefreshSucceeded = true;
        await flow.RefreshAsync("token");
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshDeniedAsync), observer.Calls);
        Assert.True(observer.LastRefreshDenied!.Value.RotationCommitted);
    }

    [Fact]
    public async Task Refresh_denied_reports_whether_the_rotation_had_committed()
    {
        // Pre-rotation deny: rotationCommitted == false — nothing changed.
        var (preFlow, _, preRefresh, preHooks, preObserver) = Build(PasswordVerificationResult.Success, null);
        preRefresh.RotateResult = RefreshValidationResult.Invalid();
        preHooks.DenyBeforeRefresh = true;
        await preFlow.RefreshAsync("token");

        // Post-rotation deny: rotationCommitted == true — a successor token exists that the client never
        // received, which calls for a different response than "nothing happened".
        var (postFlow, _, postRefresh, postHooks, postObserver) = Build(PasswordVerificationResult.Success, null);
        postRefresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());
        postHooks.DenyRefreshSucceeded = true;
        await postFlow.RefreshAsync("token");

        Assert.False(preObserver.LastRefreshDenied!.Value.RotationCommitted);
        Assert.True(postObserver.LastRefreshDenied!.Value.RotationCommitted);
    }

    [Fact]
    public async Task Observers_and_the_adopters_own_refresh_hooks_both_run()
    {
        var (flow, _, refresh, hooks, observer) = Build(PasswordVerificationResult.Success, null);
        refresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());
        await flow.RefreshAsync("token");
        Assert.Contains("refresh-succeeded", hooks.Calls);
        Assert.Contains(nameof(IIdentityEventObserver.OnRefreshSucceededAsync), observer.Calls);
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_raises_OnLogout()
    {
        var (flow, _, _, _, observer) = Build(PasswordVerificationResult.Success, null);
        await flow.LogoutAsync("token", allSessions: false);
        Assert.Contains(nameof(IIdentityEventObserver.OnLogoutAsync), observer.Calls);
        Assert.False(observer.LastLogout!.Value.AllSessions);
    }

    [Fact]
    public async Task Logout_all_sessions_is_carried_through_to_the_observer()
    {
        var (flow, _, _, _, observer) = Build(PasswordVerificationResult.Success, null);
        await flow.LogoutAsync("token", allSessions: true);
        Assert.True(observer.LastLogout!.Value.AllSessions);
    }

    [Fact]
    public async Task Logout_resolves_the_owning_user_before_revocation_and_carries_it_to_the_observer()
    {
        var (flow, _, refresh, _, observer) = Build(PasswordVerificationResult.Success, null);
        var ownerId = Guid.NewGuid();
        refresh.OwnerId = ownerId;
        await flow.LogoutAsync("token", allSessions: false);
        Assert.Equal(ownerId, observer.LastLogout!.Value.UserId);
    }

    // ── A throwing observer must not change the flow it observes ────────────

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_successful_login_result()
    {
        var withoutObserver = Build(PasswordVerificationResult.Success, NewUser());
        var withObserver = Build(PasswordVerificationResult.Success, NewUser(), new ThrowingObserver());

        var without = await withoutObserver.Flow.LoginAsync("alice", "pw");
        var with = await withObserver.Flow.LoginAsync("alice", "pw");

        Assert.Equal(without.Outcome, with.Outcome);
        Assert.Equal(without.Succeeded, with.Succeeded);
        Assert.True(with.Succeeded);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_failed_logins_outcome()
    {
        var withoutObserver = Build(PasswordVerificationResult.Failed, NewUser());
        var withObserver = Build(PasswordVerificationResult.Failed, NewUser(), new ThrowingObserver());

        var without = await withoutObserver.Flow.LoginAsync("alice", "bad");
        var with = await withObserver.Flow.LoginAsync("alice", "bad");

        // Same outcome AND no tokens either way — a throwing observer must not leak the failure reason
        // by changing what the caller sees.
        Assert.Equal(without.Outcome, with.Outcome);
        Assert.Equal(without.Tokens, with.Tokens);
        Assert.False(with.Succeeded);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_refresh_result()
    {
        var withoutObserver = Build(PasswordVerificationResult.Success, null);
        withoutObserver.Refresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());
        var withObserver = Build(PasswordVerificationResult.Success, null, new ThrowingObserver());
        withObserver.Refresh.RotateResult = RefreshValidationResult.Success(NewUser(), Successor());

        var without = await withoutObserver.Flow.RefreshAsync("token");
        var with = await withObserver.Flow.RefreshAsync("token");

        Assert.Equal(without.Outcome, with.Outcome);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_prevent_logout()
    {
        var (flow, _, refresh, _, _) = Build(PasswordVerificationResult.Success, null, new ThrowingObserver());
        await flow.LogoutAsync("token", allSessions: false);
        Assert.Equal(1, refresh.RevokeCalls);
    }
}
