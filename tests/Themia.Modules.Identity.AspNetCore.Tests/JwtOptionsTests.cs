using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class JwtOptionsTests
{
    private static JwtOptions Valid() => new()
    {
        SigningKey = new string('k', 32),
        Issuer = "themia",
        Audience = "themia-clients",
    };

    [Fact]
    public void Validate_passes_for_a_well_formed_options() => Valid().Validate();

    [Fact]
    public void Validate_rejects_a_short_signing_key()
    {
        var options = Valid();
        options.SigningKey = "too-short";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_missing_issuer()
    {
        var options = Valid();
        options.Issuer = "   ";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_missing_audience()
    {
        var options = Valid();
        options.Audience = "";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_non_positive_access_lifetime()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.Zero;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_negative_clock_skew()
    {
        var options = Valid();
        options.ClockSkew = TimeSpan.FromSeconds(-1);
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
