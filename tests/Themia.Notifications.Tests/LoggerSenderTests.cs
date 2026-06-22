using Microsoft.Extensions.Logging.Abstractions;
using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class LoggerSenderTests
{
    [Fact]
    public async Task LoggerEmail_ReturnsSuccess()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.com", Body = "hi" });
        Assert.True(r.Succeeded);
    }

    [Fact]
    public async Task LoggerSms_ReturnsSuccess()
    {
        var sut = new LoggerSmsSender(NullLogger<LoggerSmsSender>.Instance);
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.True(r.Succeeded);
    }

    [Fact]
    public async Task LoggerEmail_NullMessage_Throws()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SendAsync(null!));
    }
}
