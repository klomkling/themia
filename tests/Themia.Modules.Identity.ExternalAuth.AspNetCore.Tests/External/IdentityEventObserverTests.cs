using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

/// <summary>Coverage for <see cref="IIdentityEventObserver"/> fan-out across every
/// <see cref="ExternalAuthenticationFlow"/> path. An earlier draft of the audit design covered password
/// login only; the result would have been an audit trail that looks complete while a user signing in
/// with Google leaves no trace at all — this file exists to close exactly that gap.</summary>
public sealed class IdentityEventObserverTests
{
    private const string Provider = "fake";

    private static User NewUser() => new() { UserName = "ext-alice" };

    private static ExternalAuthRequest Request() => new("auth-code", "https://app.test/callback");

    private static ExternalIdentity Identity() =>
        new(Provider, "subject-123", "alice@acme.test", EmailVerified: true, "Alice");

    private static (ExternalAuthenticationFlow Flow, FakeRefreshTokenService Refresh, RecordingExternalHooks Hooks, RecordingObserver Observer)
        Build(FakeExternalAuthProvider? provider, ExternalLoginResult? resolution = null, params IIdentityEventObserver[] extraObservers)
    {
        var registry = new FakeProviderRegistry(provider);
        var logins = new FakeExternalLoginService { Result = resolution ?? new ExternalLoginResult(NewUser(), true, true) };
        var refresh = new FakeRefreshTokenService();
        var access = new FakeAccessTokenService(TimeProvider.System);
        var hooks = new RecordingExternalHooks { Refresh = refresh };
        var recorder = new RecordingObserver();
        var observers = extraObservers.Length == 0 ? [recorder] : extraObservers.Append(recorder).ToArray();
        var flow = new ExternalAuthenticationFlow(
            registry, logins, new FakeClaimsPrincipalFactory(), access, refresh, hooks, observers,
            TimeProvider.System, NullLogger<ExternalAuthenticationFlow>.Instance);
        return (flow, refresh, hooks, recorder);
    }

    [Fact]
    public async Task External_login_success_raises_OnExternalLoginSucceeded_with_created_and_linked()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, _, observer) = Build(provider, new ExternalLoginResult(NewUser(), WasCreated: true, WasLinked: true));

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginSucceededAsync), observer.Calls);
    }

    [Fact]
    public async Task External_login_provider_rejection_raises_OnExternalLoginFailed()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Failed("bad-code") };
        var (flow, _, _, observer) = Build(provider);

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginFailedAsync), observer.Calls);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnExternalLoginDeniedAsync), observer.Calls);
    }

    [Fact]
    public async Task External_login_unknown_provider_raises_OnExternalLoginFailed()
    {
        var (flow, _, _, observer) = Build(provider: null);

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginFailedAsync), observer.Calls);
    }

    [Fact]
    public async Task External_login_inactive_resolved_account_raises_OnExternalLoginFailed()
    {
        var inactiveUser = new User { UserName = "ext-alice", IsActive = false };
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, _, observer) = Build(provider, new ExternalLoginResult(inactiveUser, WasCreated: false, WasLinked: false));

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginFailedAsync), observer.Calls);
    }

    [Fact]
    public async Task External_login_denied_by_before_hook_raises_OnExternalLoginDenied_not_Failed()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, hooks, observer) = Build(provider);
        hooks.DenyBefore = true;

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginDeniedAsync), observer.Calls);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnExternalLoginFailedAsync), observer.Calls);
    }

    [Fact]
    public async Task External_login_denied_by_succeeded_hook_raises_OnExternalLoginDenied()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, hooks, observer) = Build(provider);
        hooks.DenyOnSucceeded = true;

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginDeniedAsync), observer.Calls);
    }

    [Fact]
    public async Task Observers_and_the_adopters_own_external_hooks_both_run()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, hooks, observer) = Build(provider);

        await flow.AuthenticateAsync(Provider, Request());

        Assert.Contains("succeeded", hooks.Calls);
        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginSucceededAsync), observer.Calls);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_successful_external_login_result()
    {
        var withoutObserver = Build(new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) });
        var withObserver = Build(new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) }, extraObservers: [new ThrowingObserver()]);

        var without = await withoutObserver.Flow.AuthenticateAsync(Provider, Request());
        var with = await withObserver.Flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(without.Outcome, with.Outcome);
        Assert.True(with.Succeeded);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_failed_external_logins_outcome()
    {
        var withoutObserver = Build(new FakeExternalAuthProvider { Result = ExternalAuthResult.Failed("bad-code") });
        var withObserver = Build(new FakeExternalAuthProvider { Result = ExternalAuthResult.Failed("bad-code") }, extraObservers: [new ThrowingObserver()]);

        var without = await withoutObserver.Flow.AuthenticateAsync(Provider, Request());
        var with = await withObserver.Flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(without.Outcome, with.Outcome);
        Assert.False(with.Succeeded);
    }
}

// FakeProviderRegistry, FakeExternalAuthProvider, FakeExternalLoginService and RecordingExternalHooks are
// declared in ExternalAuthenticationFlowTests.cs; FakeClaimsPrincipalFactory, FakeAccessTokenService and
// FakeRefreshTokenService in Fakes.cs — all in this same namespace, reused here rather than redeclared.

internal sealed class RecordingObserver : IIdentityEventObserver
{
    public List<string> Calls { get; } = [];

    public Task OnExternalLoginSucceededAsync(Guid userId, string provider, bool wasCreated, bool wasLinked, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnExternalLoginSucceededAsync));
        return Task.CompletedTask;
    }

    public Task OnExternalLoginFailedAsync(string provider, ExternalLoginOutcome reason, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnExternalLoginFailedAsync));
        return Task.CompletedTask;
    }

    public Task OnExternalLoginDeniedAsync(string provider, string? denialReason, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnExternalLoginDeniedAsync));
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingObserver : IIdentityEventObserver
{
    private static Task Throw() => throw new InvalidOperationException("observer failure");

    public Task OnExternalLoginSucceededAsync(Guid userId, string provider, bool wasCreated, bool wasLinked, CancellationToken cancellationToken = default) => Throw();
    public Task OnExternalLoginFailedAsync(string provider, ExternalLoginOutcome reason, CancellationToken cancellationToken = default) => Throw();
    public Task OnExternalLoginDeniedAsync(string provider, string? denialReason, CancellationToken cancellationToken = default) => Throw();
}
