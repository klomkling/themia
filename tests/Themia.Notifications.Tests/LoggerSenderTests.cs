using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Notifications.Providers;
using Themia.TestSupport;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class LoggerSenderTests
{
    // RETARGETED, not deleted. These two used to assert Succeeded == true. The stubs send nothing, so
    // reporting success meant a host that never configured a provider saw every send succeed while no
    // message was ever delivered — and the caller's retry and audit logic recorded deliveries that never
    // happened. "I deliberately did not send this" and "I sent this" must not be the same value.
    [Fact]
    public async Task LoggerEmail_ReportsNotConfigured_NotSuccess()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);

        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.com", Body = "hi" });

        Assert.False(r.Succeeded);
        Assert.True(r.NotConfigured);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task LoggerSms_ReportsNotConfigured_NotSuccess()
    {
        var sut = new LoggerSmsSender(NullLogger<LoggerSmsSender>.Instance);

        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });

        Assert.False(r.Succeeded);
        Assert.True(r.NotConfigured);
        Assert.NotNull(r.Error);
    }

    // Warning, not Information: Information-level filtering is common in production, and a host silently
    // getting no delivery is exactly the case that must survive it.
    [Fact]
    public async Task LoggerSms_LogsAtWarning()
    {
        var logger = new RecordingLogger<LoggerSmsSender>();
        var sut = new LoggerSmsSender(logger);

        await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+66811112222", Body = "hi" });

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task LoggerEmail_LogsAtWarning()
    {
        var logger = new RecordingLogger<LoggerEmailSender>();
        var sut = new LoggerEmailSender(logger);

        await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.com", Subject = "s", Body = "hi" });

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // The stub never logs the body. A password-reset token or an OTP code travels in it.
    [Fact]
    public async Task LoggerSenders_NeverLogTheBody()
    {
        const string body = "your code is 483920";
        var smsLogger = new RecordingLogger<LoggerSmsSender>();
        var emailLogger = new RecordingLogger<LoggerEmailSender>();

        await new LoggerSmsSender(smsLogger).SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+66811112222", Body = body });
        await new LoggerEmailSender(emailLogger).SendAsync(new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.com", Subject = "s", Body = body });

        foreach (var (_, message) in smsLogger.Entries.Concat(emailLogger.Entries))
        {
            Assert.DoesNotContain(body, message, StringComparison.Ordinal);
        }
    }

    // The subject is not logged either. It used to be, until raising this line to Warning made it survive
    // the production filtering that had been hiding it — and subjects routinely carry PII or account
    // context ("Password reset for jane.doe@acme.com", "Invoice INV-2214 for Acme Ltd").
    [Fact]
    public async Task LoggerEmail_NeverLogsTheSubject()
    {
        const string subject = "Password reset for jane.doe@acme.com";
        var logger = new RecordingLogger<LoggerEmailSender>();

        await new LoggerEmailSender(logger).SendAsync(new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "jane.doe@acme.com",
            Subject = subject,
            Body = "hi",
        });

        foreach (var (_, message) in logger.Entries)
        {
            Assert.DoesNotContain(subject, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoggerEmail_NullMessage_Throws()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SendAsync(null!));
    }
}
