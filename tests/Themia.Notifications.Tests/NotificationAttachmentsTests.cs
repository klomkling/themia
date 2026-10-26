using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

/// <summary><see cref="NotificationMessage.Attachments"/> and <see cref="EmailAttachment"/>.</summary>
public sealed class NotificationAttachmentsTests
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n"u8, .. Enumerable.Range(0, 200).Select(i => (byte)(i % 251))];

    private static EmailAttachment Receipt(string name = "RTX-2610-000123.pdf", string type = "application/pdf", byte[]? bytes = null) =>
        new(name, type, bytes ?? Pdf);

    private static NotificationMessage Email(params EmailAttachment[] attachments) => new()
    {
        Channel = NotificationChannel.Email,
        Recipient = "to@example.com",
        Subject = "Your receipt",
        Body = "<p>Attached.</p>",
        Attachments = attachments,
    };

    private static SmtpEmailSender SenderTo(string dir, Action<SmtpEmailOptions>? configure = null)
    {
        var options = new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir };
        configure?.Invoke(options);
        return new SmtpEmailSender(options, new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));
    }

    private static async Task<string> SendAndReadEmlAsync(NotificationMessage message, Action<SmtpEmailOptions>? configure = null)
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            Assert.True((await SenderTo(dir, configure).SendAsync(message)).Succeeded);
            return await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b.pdf")]
    [InlineData("a\\b.pdf")]
    [InlineData("..")]
    [InlineData("a\r\nBcc: x@y.test.pdf")]
    [InlineData("a\"b.pdf")]
    [InlineData("invoice\u202Efdp.exe")]     // right-to-left override: shows as invoiceexe.pdf
    [InlineData("a\u2066b.pdf")]             // bidi isolate
    [InlineData("a\u200Bb.pdf")]             // zero-width space
    [InlineData("a\u2028b.pdf")]             // line separator
    [InlineData("a\u2029b.pdf")]             // paragraph separator
    [InlineData("a\uE000b.pdf")]             // private use
    public void EmailAttachment_ShouldRejectAnUnsafeFileName(string name) =>
        Assert.Throws<ArgumentException>(() => Receipt(name));

    [Fact]
    public void EmailAttachment_ShouldRejectAnUnpairedSurrogateInTheFileName()
    {
        // Built here: xUnit replaces a lone surrogate in inline data with U+FFFD, which would test something else.
        Assert.Throws<ArgumentException>(() => Receipt("a" + (char)0xD800 + "b.pdf"));
        Assert.Throws<ArgumentException>(() => Receipt("a" + (char)0xDC00 + "b.pdf"));
    }

    [Fact]
    public void EmailAttachment_ShouldRejectAFileNameLongerThanTheLimit() =>
        Assert.Throws<ArgumentException>(() => Receipt(new string('a', EmailAttachment.MaxFileNameLength + 1)));

    [Theory]
    [InlineData("")]
    [InlineData("pdf")]
    [InlineData("application/")]
    [InlineData("/pdf")]
    [InlineData("application/pdf; charset=x")]
    [InlineData("application/pdf\r\nX: y")]
    [InlineData("application/pdf extra")]
    public void EmailAttachment_ShouldRejectAMalformedContentType(string type) =>
        Assert.Throws<ArgumentException>(() => Receipt(type: type));

    [Fact]
    public void EmailAttachment_ShouldRejectEmptyContent() =>
        Assert.Throws<ArgumentException>(() => Receipt(bytes: []));

    [Fact]
    public void EmailAttachment_ShouldLowerCaseTheContentType_AndAcceptAThaiName()
    {
        var attachment = Receipt("ใบเสร็จ.pdf", "Application/PDF");
        Assert.Equal("ใบกำกับภาษี 😀.pdf", Receipt("ใบกำกับภาษี 😀.pdf").FileName);

        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal("ใบเสร็จ.pdf", attachment.FileName);
    }

    [Fact]
    public void EmailAttachment_ToString_ShouldNotCarryTheBytes()
    {
        var text = Receipt().ToString();

        Assert.Contains("RTX-2610-000123.pdf", text);
        Assert.DoesNotContain(Convert.ToBase64String(Pdf)[..40], text);
        Assert.DoesNotContain("37, 80, 68", text);
    }

    [Fact]
    public void Attachments_ShouldBeCopiedOnAssignment()
    {
        var list = new List<EmailAttachment> { Receipt() };
        var message = new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.test", Attachments = list };

        list.Add(Receipt("second.pdf"));

        Assert.Single(message.Attachments!);
    }

    [Fact]
    public void Attachments_ShouldRejectANullEntry_AndTooMany()
    {
        Assert.Throws<ArgumentException>(() => new NotificationMessage { Attachments = new EmailAttachment[] { null! } });
        Assert.Throws<ArgumentException>(() => new NotificationMessage
        {
            Attachments = Enumerable.Range(0, NotificationMessage.MaxAttachments + 1).Select(i => Receipt($"{i}.pdf")).ToList(),
        });
    }

    [Fact]
    public async Task Send_ShouldWriteAMultipartMixedMessage_WithTheFileAsBase64()
    {
        var eml = await SendAndReadEmlAsync(Email(Receipt()));

        Assert.Contains("Content-Type: multipart/mixed", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/pdf; name=RTX-2610-000123.pdf", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: attachment", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Transfer-Encoding: base64", eml, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Pdf).Replace("\r\n", ""), eml.Replace("\r\n", ""), StringComparison.Ordinal);
        Assert.Contains("<p>Attached.</p>", eml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldCarryAThaiFileNameEncoded_AndKeepThePlainTextAlternative()
    {
        var message = new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "Hi",
            Body = "<p>html</p>",
            PlainTextBody = "text",
            Attachments = [Receipt("ใบกำกับภาษี.pdf")],
        };

        var eml = await SendAndReadEmlAsync(message);

        Assert.Contains("multipart/alternative", eml, StringComparison.Ordinal);
        Assert.Contains("=?utf-8?", eml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ใบกำกับภาษี", eml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldNotChangeAMessageWithoutAttachments()
    {
        var eml = await SendAndReadEmlAsync(Email());

        Assert.DoesNotContain("multipart/mixed", eml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Disposition", eml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldRefuseAContentTypeOutsideTheAllowList_UntilTheHostAllowsIt()
    {
        var png = Receipt("logo.png", "image/png");
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => SenderTo(dir).SendAsync(Email(png)));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }

        var eml = await SendAndReadEmlAsync(Email(png), o => o.AllowedAttachmentContentTypes.Add("image/png"));
        Assert.Contains("Content-Type: image/png", eml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldAllowOnlyTheListedTypes_WhenTheHostListsAny()
    {
        var pdf = Email(Receipt());
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => SenderTo(dir, o => o.AllowedAttachmentContentTypes.Add("image/png")).SendAsync(pdf));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_ShouldAllowPdf_WhenTheHostListsNothing()
    {
        Assert.Contains("RTX-2610-000123.pdf", await SendAndReadEmlAsync(Email(Receipt())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldAllowOnlyTheTypesInAppsettings_AndPdfWhenThereAreNone()
    {
        static void Bind(SmtpEmailOptions options, params string[] types) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(types.Select((t, i) => KeyValuePair.Create<string, string?>($"AllowedAttachmentContentTypes:{i}", t)))
                .Build()
                .Bind(options);

        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => SenderTo(dir, o => Bind(o, "IMAGE/PNG")).SendAsync(Email(Receipt())));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }

        Assert.Contains("image/png", await SendAndReadEmlAsync(Email(Receipt("logo.png", "image/png")), o => Bind(o, "IMAGE/PNG")), StringComparison.Ordinal);
        Assert.Contains("RTX-2610-000123.pdf", await SendAndReadEmlAsync(Email(Receipt()), o => Bind(o)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ShouldRefuseAttachmentsAboveTheCapInTotal_AndAllowThemAtIt()
    {
        var two = Email(Receipt("a.pdf", bytes: new byte[600]), Receipt("b.pdf", bytes: new byte[600]));
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => SenderTo(dir, o => o.MaxAttachmentBytes = 1_199).SendAsync(two));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }

        Assert.Contains("a.pdf", await SendAndReadEmlAsync(two, o => o.MaxAttachmentBytes = 1_200), StringComparison.Ordinal);
    }

    [Fact]
    public void SmtpEmailOptions_ShouldDefaultToPdfOnlyAndSevenMebibytes()
    {
        var options = new SmtpEmailOptions();

        Assert.Empty(options.AllowedAttachmentContentTypes);
        Assert.Equal(7L * 1024 * 1024, options.MaxAttachmentBytes);
    }

    [Fact]
    public async Task LoggerSender_ShouldIgnoreAttachments_AndNotLogTheirBytes()
    {
        var logs = new List<string>();
        var sender = new LoggerEmailSender(new ListLogger<LoggerEmailSender>(logs));

        var result = await sender.SendAsync(Email(Receipt()));

        Assert.False(result.Succeeded);
        Assert.All(logs, l => Assert.DoesNotContain(Convert.ToBase64String(Pdf)[..40], l));
        Assert.All(logs, l => Assert.DoesNotContain("RTX-2610", l));
    }

    private sealed class ListLogger<T>(List<string> lines) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
