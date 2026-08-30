using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

/// <summary>
/// <see cref="NotificationMessage.PlainTextBody"/> — the text/plain alternative that turns an HTML mail
/// into <c>multipart/alternative</c>.
/// </summary>
public sealed class NotificationPlainTextBodyTests
{
    private const string HtmlBody = "<p>ยืนยันอีเมล</p><p><a href=\"https://x.test/v\">ยืนยัน</a></p>";
    private const string PlainBody = "ยืนยันอีเมล\n\nhttps://x.test/v\n\nIf you did not request this, ignore it.";

    private static SmtpEmailSender SenderTo(string dir, bool isBodyHtml = true) =>
        new(new SmtpEmailOptions
        {
            Host = "localhost",
            FromAddress = "noreply@themia.test",
            PickupDirectory = dir,
            IsBodyHtml = isBodyHtml,
        },
            new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

    private static async Task<string> SendAndReadEmlAsync(NotificationMessage message, bool isBodyHtml = true)
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            Assert.True((await SenderTo(dir, isBodyHtml).SendAsync(message)).Succeeded);
            return await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Parts are base64 with CRLF wrapping at 76 chars, so both sides are unwrapped before comparing.
    private static bool EmlCarries(string eml, string content)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        return eml.Replace("\r\n", "").Contains(expected.Replace("\r\n", ""), StringComparison.Ordinal);
    }

    private static NotificationMessage Email(string? plainTextBody, string? body = HtmlBody, string? template = null, object? model = null) =>
        new()
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "Hi",
            Body = body,
            Template = template,
            Model = model,
            PlainTextBody = plainTextBody,
        };

    [Fact]
    public async Task PlainTextBody_ProducesMultipartAlternative_WithBothParts()
    {
        var eml = await SendAndReadEmlAsync(Email(PlainBody));

        Assert.Contains("Content-Type: multipart/alternative", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/html; charset=utf-8", eml, StringComparison.Ordinal);
        Assert.True(EmlCarries(eml, PlainBody), "the text/plain part should carry PlainTextBody verbatim");
        Assert.True(EmlCarries(eml, HtmlBody), "the text/html part should carry the rendered body");
    }

    [Fact]
    public async Task PlainPart_ComesBeforeHtmlPart()
    {
        var eml = await SendAndReadEmlAsync(Email(PlainBody));

        // RFC 2046: alternatives are ordered by increasing preference, so the richest form goes LAST.
        // Reversed, every HTML-capable client shows plain text — and nothing errors, nothing warns, the
        // mail sends. The only signal is that everyone sees the wrong version.
        var plainAt = eml.IndexOf("text/plain", StringComparison.Ordinal);
        var htmlAt = eml.IndexOf("text/html", StringComparison.Ordinal);

        // Both indices are checked for presence FIRST. Comparing them straight away would let this pass
        // when the plain part is missing entirely: IndexOf returns -1, and -1 < htmlAt is true. An
        // ordering assertion that holds when one side is absent cannot catch the regression it exists for.
        Assert.True(plainAt >= 0, "the text/plain part is missing");
        Assert.True(htmlAt >= 0, "the text/html part is missing");
        Assert.True(plainAt < htmlAt, "text/plain must precede text/html");
    }

    [Fact]
    public async Task Multipart_CarriesExactlyTwoParts()
    {
        var eml = await SendAndReadEmlAsync(Email(PlainBody));

        // "Two forms of one message" means TWO parts. Setting MailMessage.Body alongside the alternate
        // views adds a THIRD — measured: a text/plain; charset=us-ascii part, inserted ahead of both,
        // carrying the HTML source as literal text. Nothing errors and the mail still sends, so only a
        // count catches it. Three Content-Type lines: the multipart container plus one per part.
        var contentTypes = eml.Split("\r\n").Count(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(3, contentTypes);
        Assert.DoesNotContain("charset=us-ascii", eml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThaiSurvivesBothParts()
    {
        var eml = await SendAndReadEmlAsync(Email(PlainBody));

        Assert.True(EmlCarries(eml, PlainBody));
        Assert.True(EmlCarries(eml, HtmlBody));
    }

    [Fact]
    public async Task Absent_LeavesTheMessageSinglePart()
    {
        var message = Email(plainTextBody: null);

        Assert.Null(message.PlainTextBody);

        var eml = await SendAndReadEmlAsync(message);
        Assert.DoesNotContain("multipart/alternative", eml, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/html", eml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainTextBody_AppliesEvenWhenIsBodyHtmlIsFalse()
    {
        // Supplying a text alternative DECLARES that Body is the HTML form, so the deployment-wide
        // IsBodyHtml cannot quietly turn that into a no-op. Without this, one config flag would silently
        // discard the alternative on the hosts most likely to have set it by mistake.
        var eml = await SendAndReadEmlAsync(Email(PlainBody), isBodyHtml: false);

        Assert.Contains("Content-Type: multipart/alternative", eml, StringComparison.Ordinal);
        Assert.True(EmlCarries(eml, HtmlBody));
    }

    [Fact]
    public async Task PlainTextBody_IsTemplateRendered_LikeSubject()
    {
        var eml = await SendAndReadEmlAsync(Email("Hello {{name}}, verify at https://x.test/v", model: new { name = "Sam" }));

        Assert.True(EmlCarries(eml, "Hello Sam, verify at https://x.test/v"));
    }

    [Fact]
    public async Task PlainTextBody_WithNullModel_DoesNotLeakTokens()
    {
        var eml = await SendAndReadEmlAsync(Email("Order {{id}} shipped", model: null));

        Assert.False(EmlCarries(eml, "Order {{id}} shipped"), "unrendered tokens must not reach the recipient");
    }

    [Fact]
    public async Task PlainTextBody_PairsWithATemplateRenderedHtmlBody()
    {
        var eml = await SendAndReadEmlAsync(
            Email("Hello Sam", body: null, template: "<p>Hello {{name}}</p>", model: new { name = "Sam" }));

        Assert.Contains("Content-Type: multipart/alternative", eml, StringComparison.Ordinal);
        Assert.True(EmlCarries(eml, "<p>Hello Sam</p>"));
        Assert.True(EmlCarries(eml, "Hello Sam"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPlainTextBody_IsRejected(string plain)
        => Assert.Throws<ArgumentException>(() => Email(plain));

    [Fact]
    public async Task LoggerEmailSender_IgnoresPlainTextBody()
    {
        var r = await new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance).SendAsync(Email(PlainBody));

        Assert.Equal(NotificationOutcome.NotConfigured, r.Outcome);
    }
}
