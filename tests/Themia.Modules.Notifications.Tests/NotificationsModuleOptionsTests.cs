using Themia.Modules.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests;

public class NotificationsModuleOptionsTests
{
    [Fact]
    public void Validate_ShouldThrow_WhenConnectionStringNameBlank()
    {
        var options = new NotificationsModuleOptions { ConnectionStringName = " " };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldThrow_WhenDrainIntervalNotPositive(int seconds)
    {
        var options = new NotificationsModuleOptions { DrainIntervalSeconds = seconds };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenMaxBatchSizeNotPositive()
    {
        var options = new NotificationsModuleOptions { MaxBatchSize = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_ShouldPass_WithDefaults()
    {
        var options = new NotificationsModuleOptions();
        options.Validate(); // does not throw
    }
}
