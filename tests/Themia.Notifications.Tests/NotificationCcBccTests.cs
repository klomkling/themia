using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

/// <summary>
/// <see cref="NotificationMessage.Cc"/> / <see cref="NotificationMessage.Bcc"/> — the typed carbon-copy
/// seam that replaces the never-read <c>Metadata["cc"]</c> the docs promised.
/// </summary>
public sealed class NotificationCcBccTests
{
    private static SmtpEmailSender SenderTo(string pickupDirectory) =>
        new(new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = pickupDirectory },
            new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

    private static async Task<string> SendAndReadEmlAsync(NotificationMessage message)
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var result = await SenderTo(dir).SendAsync(message);
            Assert.True(result.Succeeded);
            return await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static NotificationMessage Email(IReadOnlyList<string>? cc = null, IReadOnlyList<string>? bcc = null) =>
        new()
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "Hi",
            Body = "<p>x</p>",
            Cc = cc,
            Bcc = bcc,
        };

    [Fact]
    public async Task Cc_IsWrittenToTheCcHeader_InOrder()
    {
        var text = await SendAndReadEmlAsync(Email(cc: ["one@example.com", "two@example.com"]));

        Assert.Contains("Cc: one@example.com, two@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bcc_DoesNotAppearAsABccHeader()
    {
        var text = await SendAndReadEmlAsync(Email(bcc: ["secret@example.com"]));

        // The whole point of a blind copy: no Bcc header is written, so the visible recipients cannot
        // learn who else received it.
        Assert.DoesNotContain("Bcc:", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bcc_StillReachesDelivery()
    {
        var text = await SendAndReadEmlAsync(Email(bcc: ["secret@example.com"]));

        // Pickup-directory delivery names every envelope recipient in X-Receiver so the pickup agent
        // knows where to send it -- which is also why a .eml written to disk DOES disclose the blind
        // copies, unlike a message handed to a real SMTP server. Pinned because it is the difference
        // between "bcc is hidden" and "bcc is hidden on the wire".
        Assert.Contains("X-Receiver: secret@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CcAndBcc_AreAbsentByDefault_AndTheMessageStillSends()
    {
        var message = new NotificationMessage
        { Channel = NotificationChannel.Email, Recipient = "to@example.com", Subject = "Hi", Body = "<p>x</p>" };

        Assert.Null(message.Cc);
        Assert.Null(message.Bcc);

        var text = await SendAndReadEmlAsync(message);
        Assert.DoesNotContain("Cc:", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyLists_AreAcceptedAndAddNoRecipients()
    {
        var text = await SendAndReadEmlAsync(Email(cc: [], bcc: []));

        Assert.DoesNotContain("Cc:", text, StringComparison.OrdinalIgnoreCase);
    }

    // --- CR/LF is rejected at construction, exactly as for Headers. MailAddress would also reject it at
    // send time, but only on a host that has SMTP registered: the same payload on LoggerEmailSender is
    // never parsed and never throws. Construction-time rejection does not depend on the provider.

    [Theory]
    [InlineData("a@example.com\r\nBcc: attacker@example.com")]
    [InlineData("a@example.com\nBcc: attacker@example.com")]
    public void Cc_RejectsCrLf(string address)
        => Assert.Throws<ArgumentException>(() => Email(cc: [address]));

    [Theory]
    [InlineData("a@example.com\r\nBcc: attacker@example.com")]
    [InlineData("a@example.com\nX-Injected: y")]
    public void Bcc_RejectsCrLf(string address)
        => Assert.Throws<ArgumentException>(() => Email(bcc: [address]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Cc_RejectsBlankEntry(string address)
        => Assert.Throws<ArgumentException>(() => Email(cc: [address]));

    [Fact]
    public void Cc_RejectsNullEntry()
        => Assert.Throws<ArgumentException>(() => Email(cc: [null!]));

    [Fact]
    public async Task Lists_AreCopied_SoMutatingTheCallersListCannotBypassValidation()
    {
        var callerList = new List<string> { "one@example.com" };
        var message = Email(cc: callerList);

        callerList.Add("attacker@example.com\r\nBcc: worse@example.com");
        callerList[0] = "changed@example.com";

        Assert.Equal(["one@example.com"], message.Cc);

        var text = await SendAndReadEmlAsync(message);
        Assert.DoesNotContain("attacker@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("changed@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedAddress_ThrowsFormatException_AtSend()
    {
        // Address FORMAT stays the BCL's job -- deliberately not re-implemented here. The outbox
        // dispatcher already maps FormatException to DispatchResult.Permanent, so a bad address
        // dead-letters on the first attempt instead of burning the retry cap.
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            await Assert.ThrowsAsync<FormatException>(() => SenderTo(dir).SendAsync(Email(cc: ["not-an-email"])));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoggerEmailSender_IgnoresCcAndBcc()
    {
        var r = await new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance)
            .SendAsync(Email(cc: ["c@example.com"], bcc: ["b@example.com"]));

        Assert.Equal(NotificationOutcome.NotConfigured, r.Outcome);
    }

    [Fact]
    public void Metadata_IsObsolete_SoCallersLearnItWasNeverRead()
    {
        // A string literal, not nameof: even nameof(NotificationMessage.Metadata) raises CS0618, which
        // this repo builds as an error. That is the point — a consumer on TreatWarningsAsErrors who sets
        // Metadata now fails the build rather than continuing to send into a no-op.
        var property = typeof(NotificationMessage).GetProperty("Metadata")!;
        var obsolete = property.GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(obsolete);
        Assert.Contains("Cc", obsolete.Message, StringComparison.Ordinal);
    }
}
