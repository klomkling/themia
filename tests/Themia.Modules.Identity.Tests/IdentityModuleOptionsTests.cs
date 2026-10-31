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
    public void Validate_throws_when_progressive_cap_is_below_base_lockout_duration()
    {
        var options = new IdentityModuleOptions
        {
            LockoutPolicy = LockoutPolicy.Progressive,
            LockoutDuration = TimeSpan.FromMinutes(15),
            MaxLockoutDuration = TimeSpan.FromMinutes(5),
        };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.MaxLockoutDuration), ex.ParamName);
    }

    [Fact]
    public void Validate_throws_when_progressive_cap_exceeds_one_year()
    {
        var options = new IdentityModuleOptions
        {
            LockoutPolicy = LockoutPolicy.Progressive,
            MaxLockoutDuration = TimeSpan.MaxValue,
        };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(IdentityModuleOptions.MaxLockoutDuration), ex.ParamName);
    }

    [Fact]
    public void Validate_accepts_progressive_cap_of_exactly_one_year()
    {
        var options = new IdentityModuleOptions
        {
            LockoutPolicy = LockoutPolicy.Progressive,
            MaxLockoutDuration = TimeSpan.FromDays(365),
        };
        options.Validate();
    }

    [Fact]
    public void Validate_ignores_lockout_cap_when_policy_is_not_progressive()
    {
        var options = new IdentityModuleOptions
        {
            LockoutPolicy = LockoutPolicy.KeepCount,
            LockoutDuration = TimeSpan.FromMinutes(15),
            MaxLockoutDuration = TimeSpan.FromMinutes(5),
        };
        options.Validate();
    }

    [Fact]
    public void Lockout_policy_defaults_to_reset_count_on_lock()
    {
        Assert.Equal(LockoutPolicy.ResetCountOnLock, new IdentityModuleOptions().LockoutPolicy);
        Assert.Equal(TimeSpan.FromHours(24), new IdentityModuleOptions().MaxLockoutDuration);
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

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(120)]
    public void RefreshReuseGrace_within_zero_and_two_minutes_is_valid(int seconds)
    {
        var options = new IdentityModuleOptions { RefreshReuseGrace = TimeSpan.FromSeconds(seconds) };

        options.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void RefreshReuseGrace_outside_zero_and_two_minutes_is_refused(int seconds)
    {
        var options = new IdentityModuleOptions { RefreshReuseGrace = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void RefreshReuseGrace_is_off_by_default()
    {
        Assert.Equal(TimeSpan.Zero, new IdentityModuleOptions().RefreshReuseGrace);
    }
}
