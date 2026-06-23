using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Xunit;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Tests;

public sealed class IdentityTokensServiceCollectionExtensionsTests
{
    private static void Configure(JwtOptions o)
    {
        o.SigningKey = new string('k', 32);
        o.Issuer = "themia";
        o.Audience = "themia-clients";
    }

    [Fact]
    public void AddThemiaIdentityTokens_registers_IAccessTokenService()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityTokens(Configure);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IAccessTokenService>());
    }

    [Fact]
    public void AddThemiaIdentityTokens_throws_on_invalid_options()
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<Exception>(() => services.AddThemiaIdentityTokens(o => { o.Issuer = "x"; }));
    }
}
