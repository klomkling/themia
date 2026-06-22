using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class SmtpEmailSenderTests
{
    [Fact]
    public async Task Send_WritesEmlToPickupDirectory_WithRenderedBody()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var options = new SmtpEmailOptions
            {
                Host = "localhost",
                FromAddress = "noreply@themia.test",
                PickupDirectory = dir, // test-only delivery: write .eml instead of connecting
            };
            var sut = new SmtpEmailSender(options, new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            var result = await sut.SendAsync(new NotificationMessage
            {
                Channel = NotificationChannel.Email,
                Recipient = "user@example.com",
                Subject = "Welcome {{name}}",
                Template = "<p>Hello {{name}}</p>",
                Model = new { name = "Sam" },
            });

            Assert.True(result.Succeeded);
            var eml = Directory.EnumerateFiles(dir, "*.eml").Single();
            var text = await File.ReadAllTextAsync(eml);
            Assert.Contains("user@example.com", text);
            Assert.Contains("Hello Sam", text);     // template rendered
            Assert.Contains("Welcome Sam", text);   // subject rendered too
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_RendersSubjectTemplate_EvenWhenBodyPreRendered()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var sut = new SmtpEmailSender(
                new SmtpEmailOptions { Host = "localhost", FromAddress = "noreply@themia.test", PickupDirectory = dir },
                new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            await sut.SendAsync(new NotificationMessage
            {
                Channel = NotificationChannel.Email,
                Recipient = "u@e.com",
                Subject = "Welcome {{name}}",
                Body = "<p>pre-rendered</p>",   // pre-rendered body
                Model = new { name = "Sam" },
            });

            var text = await File.ReadAllTextAsync(Directory.EnumerateFiles(dir, "*.eml").Single());
            Assert.Contains("Welcome Sam", text);      // subject rendered despite pre-rendered body
            Assert.DoesNotContain("{{name}}", text);
            Assert.Contains("pre-rendered", text);     // body used verbatim
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_NullMessage_Throws()
    {
        var sut = new SmtpEmailSender(new SmtpEmailOptions { Host = "localhost", FromAddress = "x@y.z" },
            new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SendAsync(null!));
    }
}
