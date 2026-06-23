using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;
using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests;

public sealed class ExternalAuthRegistrationTests
{
    private static void ConfigureValidJwt(Themia.Modules.Identity.Tokens.AspNetCore.Options.JwtOptions o)
    {
        o.SigningKey = new string('k', 32);
        o.Issuer = "themia";
        o.Audience = "themia-clients";
    }

    [Fact]
    public void AddThemiaExternalAuth_registers_flow_hooks_and_registry()
    {
        var services = new ServiceCollection();

        services.AddThemiaExternalAuth();

        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthenticationFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthenticationHooks));
        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthProviderRegistry));
    }

    [Fact]
    public void AddThemiaExternalAuth_called_twice_registers_flow_once()
    {
        var services = new ServiceCollection();

        services.AddThemiaExternalAuth();
        services.AddThemiaExternalAuth();

        Assert.Single(services, d => d.ServiceType == typeof(IExternalAuthenticationFlow));
    }

    [Fact]
    public void ValidateThemiaExternalAuth_passes_when_all_seams_are_present()
    {
        var services = new ServiceCollection();
        services.AddThemiaExternalAuth();
        services.AddThemiaIdentityTokens(ConfigureValidJwt); // supplies IAccessTokenService
        services.AddScoped<IExternalLoginService>(_ => new FakeExternalLoginService());
        services.AddScoped<IRefreshTokenService>(_ => new FakeRefreshTokenService());
        services.AddScoped<IClaimsPrincipalFactory>(_ => new FakeClaimsPrincipalFactory());

        var ex = Record.Exception(() => services.ValidateThemiaExternalAuth());

        Assert.Null(ex);
    }

    [Fact]
    public void ValidateThemiaExternalAuth_throws_listing_login_service_and_excludes_IUserService()
    {
        var services = new ServiceCollection();
        services.AddThemiaExternalAuth();

        var ex = Assert.Throws<InvalidOperationException>(() => services.ValidateThemiaExternalAuth());

        Assert.Contains("IExternalLoginService", ex.Message);
        Assert.DoesNotContain("IUserService", ex.Message);
    }
}
