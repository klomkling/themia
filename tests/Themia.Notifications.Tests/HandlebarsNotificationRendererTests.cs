using HandlebarsDotNet;
using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class HandlebarsNotificationRendererTests
{
    private static HandlebarsNotificationRenderer New() => new(new ThemiaNotificationsOptions());

    [Fact]
    public void Render_MergesTemplateAndModel()
    {
        var html = New().Render("<p>Hi {{name}}</p>", new { name = "Sam" });
        Assert.Equal("<p>Hi Sam</p>", html);
    }

    [Fact]
    public void Render_NullTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => New().Render(null!, new { }));
    }

    [Fact]
    public void Render_ConfigureHandlebarsHook_RegistersHelper()
    {
        var opts = new ThemiaNotificationsOptions
        {
            ConfigureHandlebars = hb => hb.RegisterHelper("shout",
                (output, _, args) => output.WriteSafeString(args[0]?.ToString()!.ToUpperInvariant())),
        };
        var html = new HandlebarsNotificationRenderer(opts).Render("{{shout name}}", new { name = "hi" });
        Assert.Equal("HI", html);
    }
}
