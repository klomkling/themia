using System.Text.RegularExpressions;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DashboardCustomStyleTests
{
    [Fact]
    public void Defaults_CustomStyleSheetAndFavicon_Empty()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.Equal(string.Empty, o.CustomStyleSheet);
        Assert.Equal(string.Empty, o.CustomFavicon);
    }

    [Fact]
    public void Page_InjectsCustomStyleSheet_AfterBuiltInCss()
    {
        var html = DashboardHtml.Page("Exceptions", "/exceptions", "<p>x</p>",
            customStyleSheet: "/app/theme.css", customFavicon: "/app/fav.ico");

        Assert.Contains("href=\"/app/theme.css\"", html);
        Assert.Contains("<link rel=\"icon\" href=\"/app/fav.ico\">", html);
        // The custom stylesheet must come AFTER the built-in dashboard.css so its rules override.
        Assert.True(
            html.IndexOf("/dashboard.css", System.StringComparison.Ordinal)
                < html.IndexOf("/app/theme.css", System.StringComparison.Ordinal),
            "custom stylesheet must be injected after the built-in dashboard.css");
    }

    [Fact]
    public void Page_OmitsCustomLinks_WhenNotConfigured()
    {
        var html = DashboardHtml.Page("Exceptions", "/exceptions", "<p>x</p>");

        Assert.DoesNotContain("rel=\"icon\"", html);
        // Only the built-in stylesheet link is present.
        Assert.Single(Regex.Matches(html, "rel=\"stylesheet\""));
    }

    [Fact]
    public void Page_EncodesCustomHrefs()
    {
        var html = DashboardHtml.Page("Exceptions", "/exceptions", "<p>x</p>",
            customStyleSheet: "\"/x\"><script>alert(1)</script>", customFavicon: "\"/f\">");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
