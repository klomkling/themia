using Themia.Modules.Notifications.Entities;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Entities;

/// <summary>Pins the integer values of <see cref="OutboxStatus"/>. The per-engine claim SQL hard-codes
/// these literals (status IN (0, 3), status = 1, etc.), so a reorder must break this test, not silently
/// desync the dialects.</summary>
public class OutboxStatusTests
{
    [Fact]
    public void Enum_values_are_pinned()
    {
        Assert.Equal(0, (int)OutboxStatus.Pending);
        Assert.Equal(1, (int)OutboxStatus.Sending);
        Assert.Equal(2, (int)OutboxStatus.Sent);
        Assert.Equal(3, (int)OutboxStatus.Failed);
        Assert.Equal(4, (int)OutboxStatus.Dead);
    }
}
