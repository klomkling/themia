using Themia.Modules.Identity.Abstractions;
using Xunit;

namespace Themia.Modules.Identity.Tests;

public class IdentityModuleOptionsTests
{
    [Fact]
    public void Validate_throws_when_max_failed_is_zero()
    {
        var options = new IdentityModuleOptions { MaxFailedAccessAttempts = 0 };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.MaxFailedAccessAttempts), ex.ParamName);
    }

    [Fact]
    public void Validate_throws_when_lockout_duration_not_positive()
    {
        var options = new IdentityModuleOptions { LockoutDuration = TimeSpan.Zero };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.LockoutDuration), ex.ParamName);
    }

    [Fact]
    public void Validate_throws_when_token_lifetime_not_positive()
    {
        var options = new IdentityModuleOptions { DefaultTokenLifetime = TimeSpan.FromSeconds(-1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.DefaultTokenLifetime), ex.ParamName);
    }

    [Fact]
    public void Validate_throws_when_connection_string_name_blank()
    {
        var options = new IdentityModuleOptions { ConnectionStringName = "  " };
        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.ConnectionStringName), ex.ParamName);
    }

    [Fact]
    public void Validate_passes_for_defaults()
    {
        var options = new IdentityModuleOptions();
        options.Validate();
    }
}
