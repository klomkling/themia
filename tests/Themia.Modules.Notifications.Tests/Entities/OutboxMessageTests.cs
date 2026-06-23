using Themia.Modules.Notifications.Entities;
using Themia.Notifications;            // NotificationChannel (neutral core)
using Xunit;

namespace Themia.Modules.Notifications.Tests.Entities;

public class OutboxMessageTests
{
    [Fact]
    public void New_pending_message_has_zero_attempts_and_pending_status()
    {
        var msg = new OutboxMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "hi",
            Body = "<p>hi</p>",
            CreatedAt = DateTimeOffset.UnixEpoch,
            NextAttemptAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(OutboxStatus.Pending, msg.Status);
        Assert.Equal(0, msg.Attempts);
        Assert.Null(msg.SentAt);
        Assert.Null(msg.LeaseOwner);
    }
}
