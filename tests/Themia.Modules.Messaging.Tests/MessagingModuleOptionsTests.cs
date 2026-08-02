using Themia.Modules.Messaging;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

public class MessagingModuleOptionsTests
{
    [Fact]
    public void Validate_ShouldSucceed_WithDefaults()
    {
        var options = new MessagingModuleOptions();

        var exception = Record.Exception(options.Validate);

        Assert.Null(exception);
    }

    // ConnectionStringName is the last required string on these options: Origin moved to
    // MessagingIdentity, which validates it in its own constructor.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenConnectionStringNameIsMissing(string? name)
    {
        var options = new MessagingModuleOptions { ConnectionStringName = name! };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal("ConnectionStringName", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldThrow_WhenMaxBatchSizeIsNotPositive(int value)
    {
        var options = new MessagingModuleOptions { MaxBatchSize = value };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    // The inbox window must outlast any redelivery the outbox can produce, or a late redelivery is
    // processed as new. Reject the configuration that guarantees that failure.
    [Fact]
    public void Validate_ShouldThrow_WhenInboxRetentionIsShorterThanDeadRetention()
    {
        var options = new MessagingModuleOptions { InboxRetentionDays = 5, DeadRetentionDays = 90 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
