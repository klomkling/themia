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
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "/app/theme.css", "/app/fav.ico"), "<p>x</p>");

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
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", ""), "<p>x</p>");

        Assert.DoesNotContain("rel=\"icon\"", html);
        // Only the built-in stylesheet link is present.
        Assert.Single(Regex.Matches(html, "rel=\"stylesheet\""));
    }

    [Fact]
    public void Page_ResolvesRelativeCustomUrls_AgainstMountPath()
    {
        // A page-relative URL would resolve differently on /exceptions vs /exceptions/{guid}; prefixing the
        // mount path (like the built-in dashboard.css) makes it load on both.
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "theme.css", "fav.ico"), "<p>x</p>");

        Assert.Contains("href=\"/exceptions/theme.css\"", html);
        Assert.Contains("href=\"/exceptions/fav.ico\"", html);
    }

    [Fact]
    public void Page_LeavesRootRelativeAndAbsoluteCustomUrls_Verbatim()
    {
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "/css/theme.css", "https://cdn.example/fav.ico"), "<p>x</p>");

        Assert.Contains("href=\"/css/theme.css\"", html);
        Assert.Contains("href=\"https://cdn.example/fav.ico\"", html);
    }

    [Fact]
    public void Page_EncodesCustomHrefs()
    {
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "\"/x\"><script>alert(1)</script>", "\"/f\">"), "<p>x</p>");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
