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
        Assert.Contains("<link rel=\"icon\" type=\"image/x-icon\" href=\"/app/fav.ico\">", html);
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

    [Theory]
    [InlineData("/app/fav.svg", "image/svg+xml")]
    [InlineData("/app/fav.png", "image/png")]
    [InlineData("/app/fav.ico", "image/x-icon")]
    [InlineData("/app/fav.gif", "image/gif")]
    [InlineData("/app/fav.jpg", "image/jpeg")]
    [InlineData("/app/fav.webp", "image/webp")]
    [InlineData("/app/fav.SVG?v=2#h", "image/svg+xml")]
    public void Page_DerivesFaviconType_FromUrlExtension(string favicon, string expectedType)
    {
        // A browser uses the type hint to decide it can render the icon at all — an SVG favicon served
        // without type="image/svg+xml" may be skipped entirely, leaving the dashboard with no icon.
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", favicon), "<p>x</p>");

        Assert.Contains($"<link rel=\"icon\" type=\"{expectedType}\" href=\"", html);
    }

    [Fact]
    public void Page_OmitsFaviconType_WhenExtensionIsUnrecognized()
    {
        // Guessing a wrong type is worse than omitting it — the browser sniffs when there is no hint.
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", "/app/icon"), "<p>x</p>");

        Assert.Contains("<link rel=\"icon\" href=\"/app/icon\">", html);
    }

    [Fact]
    public void Page_EncodesCustomHrefs()
    {
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "\"/x\"><script>alert(1)</script>", "\"/f\">"), "<p>x</p>");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
