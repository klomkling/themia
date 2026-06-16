using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests.External;

public sealed class ExternalAuthenticationFlowTests
{
    private const string Provider = "fake";

    private static User NewUser() => new() { UserName = "ext-alice" };

    private static (ExternalAuthenticationFlow Flow, FakeExternalLoginService Logins, FakeRefreshTokenService Refresh, FakeAccessTokenService Access, RecordingExternalHooks Hooks)
        Build(
            FakeExternalAuthProvider? provider,
            ExternalLoginResult? resolution = null,
            TimeProvider? clock = null)
    {
        var timeProvider = clock ?? TimeProvider.System;
        var registry = new FakeProviderRegistry(provider);
        var logins = new FakeExternalLoginService { Result = resolution ?? new ExternalLoginResult(NewUser(), true, true) };
        var refresh = new FakeRefreshTokenService();
        var access = new FakeAccessTokenService(timeProvider);
        var hooks = new RecordingExternalHooks { Refresh = refresh };
        var flow = new ExternalAuthenticationFlow(
            registry, logins, new FakeClaimsPrincipalFactory(), access, refresh, hooks, timeProvider,
            NullLogger<ExternalAuthenticationFlow>.Instance);
        return (flow, logins, refresh, access, hooks);
    }

    private static ExternalAuthRequest Request() => new("auth-code", "https://app.test/callback");

    [Fact]
    public async Task Unknown_provider_returns_provider_not_found_and_fires_failed_hook()
    {
        var (flow, logins, refresh, _, hooks) = Build(provider: null);

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(ExternalLoginOutcome.ProviderNotFound, result.Outcome);
        Assert.Equal(0, logins.ResolveCalls);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(["failed"], hooks.Calls);
        Assert.Equal(ExternalLoginOutcome.ProviderNotFound, hooks.FailedReason);
    }

    [Fact]
    public async Task Provider_rejection_returns_provider_rejected_without_resolving_or_issuing()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Failed("bad-code") };
        var (flow, logins, refresh, _, hooks) = Build(provider);

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(ExternalLoginOutcome.ProviderRejected, result.Outcome);
        Assert.Equal(0, logins.ResolveCalls);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(["before", "failed"], hooks.Calls);
        Assert.Equal(ExternalLoginOutcome.ProviderRejected, hooks.FailedReason);
    }

    [Fact]
    public async Task Before_hook_deny_returns_denied_without_exchange_and_fires_failed_hook()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, logins, refresh, _, hooks) = Build(provider);
        hooks.DenyBefore = true;

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(ExternalLoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, provider.ExchangeCalls);
        Assert.Equal(0, logins.ResolveCalls);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(["before", "failed"], hooks.Calls);
        Assert.Equal(ExternalLoginOutcome.Denied, hooks.FailedReason);
    }

    [Fact]
    public async Task Succeeded_hook_deny_returns_denied_without_issuing()
    {
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, logins, refresh, _, hooks) = Build(provider);
        hooks.DenyOnSucceeded = true;

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.Equal(ExternalLoginOutcome.Denied, result.Outcome);
        Assert.Equal(1, logins.ResolveCalls);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(["before", "succeeded", "failed"], hooks.Calls);
        Assert.Equal(ExternalLoginOutcome.Denied, hooks.FailedReason);
    }

    [Fact]
    public async Task Success_issues_pair_and_fires_before_then_succeeded()
    {
        var user = NewUser();
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, logins, refresh, access, hooks) = Build(provider, new ExternalLoginResult(user, true, true));

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.True(result.Succeeded);
        Assert.True(result.WasCreated);
        Assert.True(result.WasLinked);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("refresh-raw", result.Tokens!.Value.RefreshToken);
        Assert.Equal(1, access.IssueCalls);
        Assert.Equal(1, refresh.IssueCalls);
        Assert.Equal(1, logins.ResolveCalls);
        Assert.Equal(["before", "succeeded"], hooks.Calls);
        Assert.True(hooks.SucceededRanBeforeIssue);
    }

    [Fact]
    public async Task Success_reports_access_token_lifetime_in_seconds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var provider = new FakeExternalAuthProvider { Result = ExternalAuthResult.Success(Identity()) };
        var (flow, _, _, _, _) = Build(provider, clock: clock);

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(900, result.Tokens!.Value.ExpiresInSeconds); // 15 min = 900 s
    }

    private static ExternalIdentity Identity() =>
        new(Provider, "subject-123", "alice@acme.test", EmailVerified: true, "Alice");
}

internal sealed class FakeProviderRegistry(FakeExternalAuthProvider? registered) : IExternalAuthProviderRegistry
{
    private readonly FakeExternalAuthProvider? registered = registered;

    public bool TryGet(string name, [NotNullWhen(true)] out IExternalAuthProvider? provider)
    {
        provider = registered;
        return provider is not null;
    }
}

internal sealed class FakeExternalAuthProvider : IExternalAuthProvider
{
    public string Name => "fake";
    public ExternalAuthResult Result { get; set; }
    public int ExchangeCalls { get; private set; }

    public Task<ExternalAuthResult> ExchangeAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default)
    {
        ExchangeCalls++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeExternalLoginService : IExternalLoginService
{
    public ExternalLoginResult Result { get; set; }
    public int ResolveCalls { get; private set; }

    public Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ResolveCalls++;
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingExternalHooks : ExternalAuthenticationHooksBase
{
    public bool DenyBefore { get; set; }
    public bool DenyOnSucceeded { get; set; }
    public List<string> Calls { get; } = [];
    public ExternalLoginOutcome? FailedReason { get; private set; }
    public bool SucceededRanBeforeIssue { get; set; }
    public FakeRefreshTokenService? Refresh { get; set; }

    public override Task OnBeforeExternalLoginAsync(BeforeExternalLoginContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("before");
        if (DenyBefore) context.Deny("blocked");
        return Task.CompletedTask;
    }

    public override Task OnExternalLoginSucceededAsync(ExternalLoginSucceededContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("succeeded");
        if (Refresh is not null) SucceededRanBeforeIssue = Refresh.IssueCalls == 0;
        if (DenyOnSucceeded) context.Deny("gated");
        return Task.CompletedTask;
    }

    public override Task OnExternalLoginFailedAsync(ExternalLoginFailedContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("failed");
        FailedReason = context.Reason;
        return Task.CompletedTask;
    }
}
