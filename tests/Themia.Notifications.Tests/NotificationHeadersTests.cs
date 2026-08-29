using Microsoft.Extensions.Logging.Abstractions;

using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

/// <summary>
/// <see cref="NotificationMessage.Headers"/> — the per-message header seam (coord #0104), and the
/// injection/duplication guards that make it safe to expose one.
/// </summary>
public sealed class NotificationHeadersTests
{
    private static NotificationMessage EmailWithHeaders(IReadOnlyDictionary<string, string> headers) =>
        new()
        {
            Channel = NotificationChannel.Email,
            Recipient = "u@e.com",
            Subject = "Hi",
            Body = "<p>x</p>",
            Headers = headers,
        };

    [Fact]
    public async Task Send_WritesCustomHeaderToTheMimeMessage()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var sut = new SmtpEmailSender(
                new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir },
                new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            var result = await sut.SendAsync(EmailWithHeaders(new Dictionary<string, string>
            {
                ["X-SES-CONFIGURATION-SET"] = "propertiezy-transactional",
            }));

            Assert.True(result.Succeeded);
            var text = await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
            Assert.Contains("X-SES-CONFIGURATION-SET: propertiezy-transactional", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_WithoutHeaders_WritesNoExtraHeaders_AndStillSucceeds()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var sut = new SmtpEmailSender(
                new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir },
                new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            var message = new NotificationMessage
            { Channel = NotificationChannel.Email, Recipient = "u@e.com", Subject = "Hi", Body = "<p>x</p>" };

            Assert.Null(message.Headers);   // additive: absent by default, so every existing caller is unchanged
            Assert.True((await sut.SendAsync(message)).Succeeded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_EmptyHeaderDictionary_IsAcceptedAndSends()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var sut = new SmtpEmailSender(
                new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir },
                new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            Assert.True((await sut.SendAsync(EmailWithHeaders(new Dictionary<string, string>()))).Succeeded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // --- Header injection: CR/LF in a value would end the header and let a caller append arbitrary
    // headers (and, after a blank line, an arbitrary body). Rejected at construction, so it cannot
    // reach ANY sender -- not merely the SMTP one.

    [Theory]
    [InlineData("transactional\r\nBcc: attacker@example.com")]
    [InlineData("transactional\nBcc: attacker@example.com")]
    [InlineData("transactional\r")]
    public void Headers_RejectCrLfInValue(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EmailWithHeaders(new Dictionary<string, string> { ["X-SES-CONFIGURATION-SET"] = value }));
        Assert.Contains("X-SES-CONFIGURATION-SET", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Bad\r\nBcc")]
    [InlineData("X-Bad\nBcc")]
    public void Headers_RejectCrLfInName(string name)
        => Assert.Throws<ArgumentException>(() =>
            EmailWithHeaders(new Dictionary<string, string> { [name] = "v" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Headers_RejectBlankName(string name)
        => Assert.Throws<ArgumentException>(() =>
            EmailWithHeaders(new Dictionary<string, string> { [name] = "v" }));

    // --- Envelope/MIME headers the sender itself writes. A duplicate is rejected by some receivers and
    // interpreted inconsistently by others, so a custom copy is refused rather than silently winning.

    [Theory]
    [InlineData("To")]
    [InlineData("From")]
    [InlineData("Subject")]
    [InlineData("Date")]
    [InlineData("Message-ID")]
    [InlineData("MIME-Version")]
    [InlineData("Content-Type")]
    [InlineData("Content-Transfer-Encoding")]
    public void Headers_RejectReservedName(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EmailWithHeaders(new Dictionary<string, string> { [name] = "v" }));
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("MESSAGE-ID")]
    [InlineData("  From  ")]
    public void Headers_RejectReservedName_RegardlessOfCaseOrSurroundingSpace(string name)
        => Assert.Throws<ArgumentException>(() =>
            EmailWithHeaders(new Dictionary<string, string> { [name] = "v" }));

    [Fact]
    public async Task Headers_AreCopied_SoMutatingTheCallersDictionaryCannotBypassValidation()
    {
        var callerDictionary = new Dictionary<string, string> { ["X-SES-CONFIGURATION-SET"] = "transactional" };
        var message = EmailWithHeaders(callerDictionary);

        // The caller still holds the underlying Dictionary: IReadOnlyDictionary is read-only through the
        // interface only. Storing that instance would let this walk an injected value past the checks.
        callerDictionary["X-SES-CONFIGURATION-SET"] = "transactional\r\nBcc: attacker@example.com";
        callerDictionary["Bcc"] = "attacker@example.com";

        Assert.Equal("transactional", message.Headers!["X-SES-CONFIGURATION-SET"]);
        Assert.False(message.Headers.ContainsKey("Bcc"));

        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var sut = new SmtpEmailSender(
                new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir },
                new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            await sut.SendAsync(message);

            var text = await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
            Assert.DoesNotContain("attacker@example.com", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // --- Non-SMTP senders accept the property and ignore it. A consumer sets Headers unconditionally,
    // so a sender that threw on a header it does not use would turn an unconfigured-provider
    // deployment -- a state Themia documents as supported -- into a crash.

    [Fact]
    public async Task LoggerEmailSender_IgnoresHeaders()
    {
        var r = await new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance)
            .SendAsync(EmailWithHeaders(new Dictionary<string, string> { ["X-SES-CONFIGURATION-SET"] = "t" }));

        Assert.Equal(NotificationOutcome.NotConfigured, r.Outcome);
    }

    [Fact]
    public async Task LoggerSmsSender_IgnoresHeaders()
    {
        var message = new NotificationMessage
        {
            Channel = NotificationChannel.Sms,
            Recipient = "+66811112222",
            Body = "hi",
            Headers = new Dictionary<string, string> { ["X-Anything"] = "v" },
        };

        var r = await new LoggerSmsSender(NullLogger<LoggerSmsSender>.Instance).SendAsync(message);

        Assert.Equal(NotificationOutcome.NotConfigured, r.Outcome);
    }
}
