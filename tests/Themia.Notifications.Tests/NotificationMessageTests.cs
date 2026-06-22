using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class NotificationMessageTests
{
    [Fact]
    public void Message_HoldsInitOnlyValues()
    {
        var m = new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "a@b.com",
            Subject = "Hi",
            Template = "<p>{{name}}</p>",
            Model = new { name = "Sam" },
        };

        Assert.Equal(NotificationChannel.Email, m.Channel);
        Assert.Equal("a@b.com", m.Recipient);
        Assert.Equal("Hi", m.Subject);
        Assert.Null(m.Body);
    }

    [Fact]
    public void Result_SuccessAndFailureFactories()
    {
        var ok = NotificationResult.Success("id-1");
        var bad = NotificationResult.Failure("smtp down");

        Assert.True(ok.Succeeded);
        Assert.Equal("id-1", ok.ProviderMessageId);
        Assert.False(bad.Succeeded);
        Assert.Equal("smtp down", bad.Error);
    }
}
