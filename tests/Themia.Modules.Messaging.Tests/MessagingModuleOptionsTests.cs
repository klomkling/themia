using Themia.Modules.Messaging;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

public class MessagingModuleOptionsTests
{
    [Fact]
    public void Validate_ShouldSucceed_WithDefaults()
    {
        var exception = Record.Exception(() => new MessagingModuleOptions().Validate());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenConnectionStringNameIsMissing(string? name)
    {
        var options = new MessagingModuleOptions { ConnectionStringName = name! };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldThrow_WhenMaxBatchSizeIsNotPositive(int value)
    {
        var options = new MessagingModuleOptions { MaxBatchSize = value };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    // Origin identifies this service to every peer; a blank origin makes forwarded messages un-droppable
    // by the loop guard, so it is rejected rather than defaulted.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenOriginIsMissing(string? origin)
    {
        var options = new MessagingModuleOptions { Origin = origin! };

        Assert.Throws<ArgumentException>(options.Validate);
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
