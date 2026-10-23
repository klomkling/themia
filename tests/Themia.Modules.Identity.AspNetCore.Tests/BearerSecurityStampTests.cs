using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class BearerSecurityStampTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class FakeValidator(bool current) : ISecurityStampValidator
    {
        public int Calls { get; private set; }

        public Task<bool> IsCurrentAsync(Guid userId, string? stamp, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(current && userId == UserId && stamp == "stamp-1");
        }
    }

    private static async Task<(TokenValidatedContext Context, FakeValidator Validator)> RunAsync(
        bool enabled, bool stampIsCurrent, string? stampClaim = "stamp-1", string? subject = null)
    {
        var validator = new FakeValidator(stampIsCurrent);
        var services = new ServiceCollection();
        services.AddSingleton(new IdentityModuleOptions { ValidateSecurityStampOnEachRequest = enabled });
        services.AddSingleton<ISecurityStampValidator>(validator);
        var jwt = new JwtOptions { SigningKey = new string('k', 32), Issuer = "themia", Audience = "clients" };
        services.AddSingleton(jwt);
        services.AddSingleton<IJwtSigningCredentialsProvider>(new SymmetricSigningCredentialsProvider(jwt));
        services.AddAuthentication().AddThemiaJwtBearer();
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        var claims = new List<Claim> { new("sub", subject ?? UserId.ToString()) };
        if (stampClaim is not null)
        {
            claims.Add(new Claim(IdentityClaimTypes.SecurityStamp, stampClaim));
        }

        var http = new DefaultHttpContext { RequestServices = provider };
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new TokenValidatedContext(http, scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        await options.Events.OnTokenValidated(context);
        return (context, validator);
    }

    [Fact]
    public async Task Token_with_a_current_stamp_is_accepted()
    {
        var (context, validator) = await RunAsync(enabled: true, stampIsCurrent: true);

        Assert.Null(context.Result?.Failure);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task Token_with_a_stale_stamp_is_refused()
    {
        var (context, _) = await RunAsync(enabled: true, stampIsCurrent: false);

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public async Task Token_without_a_stamp_claim_is_refused_when_the_check_is_on()
    {
        var (context, _) = await RunAsync(enabled: true, stampIsCurrent: true, stampClaim: null);

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public async Task Token_with_a_non_guid_subject_is_refused()
    {
        var (context, _) = await RunAsync(enabled: true, stampIsCurrent: true, subject: "not-a-guid");

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public async Task Nothing_is_checked_when_the_option_is_off()
    {
        var (context, validator) = await RunAsync(enabled: false, stampIsCurrent: false);

        Assert.Null(context.Result?.Failure);
        Assert.Equal(0, validator.Calls);
    }
}
