using HandlebarsDotNet;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class HandlebarsHtmlTemplateRendererTests
{
    [Fact]
    public void Render_MergesModelIntoTemplate()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        var html = sut.Render("<p>{{name}}</p>", new { name = "Ada" });

        Assert.Equal("<p>Ada</p>", html);
    }

    [Fact]
    public void Render_EmptyIfNullHelper_RendersEmptyForNull()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        var html = sut.Render("[{{emptyIfNull missing}}]", new { missing = (string?)null });

        Assert.Equal("[]", html);
    }

    [Fact]
    public void Render_ConfigureHandlebars_RegistersCustomHelper()
    {
        var options = new ThemiaPdfOptions
        {
            ConfigureHandlebars = hbs => hbs.RegisterHelper(
                "shout",
                (output, _, args) => output.WriteSafeString(args[0]?.ToString()?.ToUpperInvariant() ?? "")),
        };
        var sut = new HandlebarsHtmlTemplateRenderer(options);

        var html = sut.Render("{{shout name}}", new { name = "hi" });

        Assert.Equal("HI", html);
    }

    [Fact]
    public void Render_NullTemplate_Throws()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        Assert.Throws<ArgumentNullException>((Action)(() => sut.Render(null!, new { })));
    }
}
