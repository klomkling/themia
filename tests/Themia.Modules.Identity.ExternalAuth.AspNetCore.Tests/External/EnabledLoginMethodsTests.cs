using Microsoft.Extensions.Logging.Abstractions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

public sealed class EnabledLoginMethodsTests
{
    private const string Provider = "fake";
    private const LoginMethods AllMethods = LoginMethods.Password | LoginMethods.Otp | LoginMethods.External;

    private static (ExternalAuthenticationFlow Flow, FakeExternalLoginService Logins, FakeRefreshTokenService Refresh, RecordingExternalHooks Hooks)
        Build(LoginMethods enabled, FakeExternalAuthProvider? provider)
    {
        var logins = new FakeExternalLoginService { Result = new ExternalLoginResult(new User { UserName = "ext-alice" }, true, true) };
        var refresh = new FakeRefreshTokenService();
        var hooks = new RecordingExternalHooks { Refresh = refresh };
        var flow = new ExternalAuthenticationFlow(
            new FakeProviderRegistry(provider), logins, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(TimeProvider.System),
            refresh, hooks, [], new IdentityModuleOptions { EnabledLoginMethods = enabled }, TimeProvider.System,
            NullLogger<ExternalAuthenticationFlow>.Instance);
        return (flow, logins, refresh, hooks);
    }

    private static ExternalAuthRequest Request() => new("auth-code", "https://app.test/callback");

    private static FakeExternalAuthProvider GoodProvider() =>
        new() { Result = ExternalAuthResult.Success(new ExternalIdentity(Provider, "subject-123", "alice@acme.test", EmailVerified: true, "Alice")) };

    [Fact]
    public async Task ExternalLogin_WithExternalOff_IssuesNoTokensEvenForAValidProviderCode()
    {
        var (flow, logins, refresh, _) = Build(LoginMethods.Password | LoginMethods.Otp, GoodProvider());

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.False(result.Succeeded);
        Assert.Equal(0, logins.ResolveCalls);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task ExternalLogin_WithExternalOff_IsRefusedLikeAProviderRejection()
    {
        var rejecting = new FakeExternalAuthProvider { Result = ExternalAuthResult.Failed("bad-code") };
        var (off, _, _, _) = Build(LoginMethods.Password, GoodProvider());
        var (on, _, _, _) = Build(AllMethods, rejecting);

        var disabled = await off.AuthenticateAsync(Provider, Request());
        var rejected = await on.AuthenticateAsync(Provider, Request());

        Assert.Equal(ExternalLoginOutcome.ProviderRejected, rejected.Outcome);
        Assert.Equal(rejected.Outcome, disabled.Outcome);
    }

    [Fact]
    public async Task ExternalLogin_WithExternalOff_AnUnknownProviderDoesNotGetADifferentAnswer()
    {
        var (flow, _, _, _) = Build(LoginMethods.Password, provider: null);

        var result = await flow.AuthenticateAsync("not-registered", Request());

        Assert.Equal(ExternalLoginOutcome.ProviderRejected, result.Outcome);
    }

    [Fact]
    public async Task ExternalLogin_WithExternalOn_StillSucceeds()
    {
        var (flow, _, refresh, _) = Build(LoginMethods.External, GoodProvider());

        var result = await flow.AuthenticateAsync(Provider, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(1, refresh.IssueCalls);
    }
}
