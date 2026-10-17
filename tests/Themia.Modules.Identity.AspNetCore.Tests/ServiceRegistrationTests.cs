using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Themia.Modules.Identity.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class ServiceRegistrationTests
{
    private static void Configure(JwtOptions o)
    {
        o.SigningKey = new string('k', 32);
        o.Issuer = "themia";
        o.Audience = "clients";
    }

    /// <summary>Registers the core Identity services so the AspNetCore prerequisite guard passes.</summary>
    private static ServiceCollection WithCoreIdentity()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityCore(o => { });
        return services;
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_validates_and_registers_services()
    {
        var services = WithCoreIdentity();
        services.AddThemiaIdentityAspNetCore(Configure);

        Assert.Contains(services, d => d.ServiceType == typeof(JwtOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(IJwtSigningCredentialsProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IAccessTokenService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationHooks));

        // The external-login flow/hooks are now owned by AddThemiaExternalAuth (ExternalAuth package),
        // not AddThemiaIdentityAspNetCore — so they are intentionally absent here.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IExternalAuthenticationFlow));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IExternalAuthenticationHooks));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_throws_on_invalid_options()
    {
        var services = WithCoreIdentity();
        Assert.Throws<ArgumentException>(() => services.AddThemiaIdentityAspNetCore(o => { o.Issuer = "x"; }));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_does_not_overwrite_a_custom_hooks_registration()
    {
        var services = WithCoreIdentity();
        services.AddSingleton<IAuthenticationHooks, AuthenticationHooksBase>();
        services.AddThemiaIdentityAspNetCore(Configure);
        Assert.Single(services, d => d.ServiceType == typeof(IAuthenticationHooks));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_throws_when_core_identity_services_are_missing()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddThemiaIdentityAspNetCore(Configure));
    }

    [Fact]
    public void AccessTokenService_resolves_with_logging_on_a_bare_service_collection()
    {
        var services = WithCoreIdentity();
        services.AddThemiaIdentityAspNetCore(Configure);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAccessTokenService>());
    }

    private sealed class NoopOtpSender : IOtpCodeSender
    {
        public Task SendAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static byte[] Key() => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void AddThemiaIdentityOtpLogin_registers_the_flow_and_challenge_service()
    {
        var services = WithCoreIdentity();
        services.AddThemiaIdentityAspNetCore(Configure);
        services.AddSingleton<IOtpCodeSender, NoopOtpSender>();

        services.AddThemiaIdentityOtpLogin(o => o.CodeHashKey = Key());

        Assert.Contains(services, d => d.ServiceType == typeof(IOtpLoginFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IOtpChallengeService));
        Assert.Contains(services, d => d.ServiceType == typeof(IOtpRecipientResolver));
        Assert.Contains(services, d => d.ServiceType == typeof(OtpLoginOptions));
    }

    [Fact]
    public void AddThemiaIdentityOtpLogin_fails_at_registration_without_a_sender()
    {
        var services = WithCoreIdentity();
        services.AddThemiaIdentityAspNetCore(Configure);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaIdentityOtpLogin(o => o.CodeHashKey = Key()));

        Assert.Contains("IOtpCodeSender", ex.Message);
    }

    [Fact]
    public void AddThemiaIdentityOtpLogin_fails_at_registration_without_the_authentication_flow()
    {
        var services = WithCoreIdentity();
        services.AddSingleton<IOtpCodeSender, NoopOtpSender>();

        Assert.Throws<InvalidOperationException>(() => services.AddThemiaIdentityOtpLogin(o => o.CodeHashKey = Key()));
    }

    [Fact]
    public void AddThemiaIdentityOtpLogin_fails_at_registration_with_a_short_or_missing_key()
    {
        var services = WithCoreIdentity();
        services.AddThemiaIdentityAspNetCore(Configure);
        services.AddSingleton<IOtpCodeSender, NoopOtpSender>();

        Assert.Throws<ArgumentException>(() => services.AddThemiaIdentityOtpLogin(_ => { }));
        Assert.Throws<ArgumentException>(() => services.AddThemiaIdentityOtpLogin(o => o.CodeHashKey = new byte[16]));
    }
}
