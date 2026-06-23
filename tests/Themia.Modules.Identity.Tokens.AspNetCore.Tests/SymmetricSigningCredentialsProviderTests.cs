using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Xunit;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Tests;

public sealed class SymmetricSigningCredentialsProviderTests
{
    [Fact]
    public void Provides_hs256_credentials_and_matching_validation_key()
    {
        var options = new JwtOptions { SigningKey = new string('k', 32), Issuer = "i", Audience = "a" };
        var provider = new SymmetricSigningCredentialsProvider(options);

        Assert.Equal(SecurityAlgorithms.HmacSha256, provider.SigningCredentials.Algorithm);
        Assert.Same(provider.SigningCredentials.Key, provider.ValidationKey);
        Assert.IsType<SymmetricSecurityKey>(provider.ValidationKey);
    }
}
