using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
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

    [Fact]
    public void AddThemiaIdentityAspNetCore_validates_and_registers_services()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityAspNetCore(Configure);

        Assert.Contains(services, d => d.ServiceType == typeof(JwtOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(IJwtSigningCredentialsProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IAccessTokenService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationHooks));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_throws_on_invalid_options()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddThemiaIdentityAspNetCore(o => { o.Issuer = "x"; }));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_does_not_overwrite_a_custom_hooks_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationHooks, AuthenticationHooksBase>();
        services.AddThemiaIdentityAspNetCore(Configure);
        Assert.Single(services, d => d.ServiceType == typeof(IAuthenticationHooks));
    }
}
