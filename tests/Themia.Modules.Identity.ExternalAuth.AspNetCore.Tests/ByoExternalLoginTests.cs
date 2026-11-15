using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;
using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests;

/// <summary>Proves the bring-your-own (BYO) external-login path: the flow authenticates and issues a
/// real access token with only the external seams registered — no <c>AddThemiaIdentity</c>, no
/// <c>IUserService</c>, no Identity persistence.</summary>
public sealed class ByoExternalLoginTests
{
    private const string Provider = "test";

    private static void ConfigureJwt(JwtOptions o)
    {
        o.SigningKey = new string('k', 32);
        o.Issuer = "themia";
        o.Audience = "themia-clients";
    }

    private static ExternalIdentity IdentityFor(string subject) =>
        new(Provider, subject, "byo@example.com", EmailVerified: true, "BYO User");

    [Fact]
    public async Task External_login_succeeds_with_byo_seams_and_no_identity_persistence()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaIdentityTokens(ConfigureJwt); // supplies the real (defaulted) IAccessTokenService
        services.AddThemiaExternalAuth()
            .AddProvider(new FakeExternalAuthProvider(Provider, IdentityFor("u-1")));
        services.AddScoped<IExternalLoginService>(_ => new StubExternalLoginService());
        services.AddScoped<IRefreshTokenService>(_ => new FakeRefreshTokenService());
        services.AddScoped<IClaimsPrincipalFactory>(_ => new FakeClaimsPrincipalFactory());

        // NOTE: no AddThemiaIdentity, no IUserService. The guard asserts the BYO seams suffice.
        services.ValidateThemiaExternalAuth();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var flow = scope.ServiceProvider.GetRequiredService<IExternalAuthenticationFlow>();

        var result = await flow.AuthenticateAsync(
            Provider,
            new ExternalAuthRequest("auth-code", "https://app.example/callback"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        // The access token was minted by the real IAccessTokenService from AddThemiaIdentityTokens,
        // with no Identity persistence / IUserService in the container.
        Assert.NotNull(result.Tokens);
        Assert.False(string.IsNullOrEmpty(result.Tokens!.Value.AccessToken));
    }

    [Fact]
    public async Task External_login_reloads_the_user_after_the_hook_when_the_container_has_a_user_service()
    {
        var resolved = new User { UserName = "sub-1" };
        resolved.SetId(Guid.NewGuid());
        resolved.TenantId = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme");
        var store = new FakeUserStore { Stored = resolved };
        var principals = new FakeClaimsPrincipalFactory();
        var hooks = new HookThatRotatesTheStamp(store, resolved);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaIdentityTokens(ConfigureJwt);
        services.AddThemiaExternalAuth().AddProvider(new FakeExternalAuthProvider(Provider, IdentityFor("sub-1")));
        services.AddScoped<IExternalLoginService>(_ => new FixedExternalLoginService(resolved));
        services.AddScoped<IRefreshTokenService>(_ => new FakeRefreshTokenService());
        services.AddScoped<IClaimsPrincipalFactory>(_ => principals);
        services.AddSingleton<IUserService>(store);
        services.AddSingleton(new IdentityModuleOptions()); // what AddThemiaIdentity registers; the greediest flow constructor needs it
        services.AddSingleton<IExternalAuthenticationHooks>(hooks);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var flow = scope.ServiceProvider.GetRequiredService<IExternalAuthenticationFlow>();

        var result = await flow.AuthenticateAsync(Provider, new ExternalAuthRequest("auth-code", "https://app.example/callback"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Same(hooks.Reloaded, principals.LastUser);
    }

    private sealed class FixedExternalLoginService(User user) : IExternalLoginService
    {
        public Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalLoginResult(user, WasCreated: false, WasLinked: false));
    }

    private sealed class HookThatRotatesTheStamp(FakeUserStore store, User before) : ExternalAuthenticationHooksBase
    {
        public User? Reloaded { get; private set; }

        public override Task OnExternalLoginSucceededAsync(ExternalLoginSucceededContext context, CancellationToken cancellationToken = default)
        {
            Reloaded = ReloadedUser.After(before);
            store.Stored = Reloaded;
            return Task.CompletedTask;
        }
    }

    /// <summary>A provider that returns a fixed validated identity, bypassing any real code exchange.</summary>
    private sealed class FakeExternalAuthProvider(string name, ExternalIdentity identity) : IExternalAuthProvider
    {
        public string Name { get; } = name;

        public Task<ExternalAuthResult> ExchangeAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalAuthResult.Success(identity));
    }

    /// <summary>A user-store seam that resolves an active user without touching any persistence.</summary>
    private sealed class StubExternalLoginService : IExternalLoginService
    {
        public Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity identity, CancellationToken cancellationToken = default)
        {
            var user = new User { UserName = identity.Subject, Email = identity.Email };
            user.SetId(Guid.NewGuid());
            return Task.FromResult(new ExternalLoginResult(user, WasCreated: true, WasLinked: true));
        }
    }
}
