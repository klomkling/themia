using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

/// <summary>The two raw-HTML chrome slots. Unlike every other string on the page these are emitted
/// verbatim — they are adopter-authored markup (a header bar, a back-link, a viewport meta), not
/// attacker-influenceable data.</summary>
public sealed class DashboardChromeSlotsTests
{
    [Fact]
    public void Defaults_HeadHtmlAndBodyStartHtml_Empty()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.Equal(string.Empty, o.HeadHtml);
        Assert.Equal(string.Empty, o.BodyStartHtml);
    }

    [Fact]
    public void Page_EmitsHeadHtmlVerbatim_AtEndOfHead()
    {
        // A marker that cannot collide with the page's own default chrome — asserting position by
        // IndexOf against a tag the dashboard also emits itself (e.g. the viewport meta) would find the
        // built-in occurrence and silently test nothing.
        const string head = "<script src=\"/app/chrome.js\"></script>";
        var html = DashboardHtml.Page(
            new DashboardChrome("Exceptions", "/exceptions", "/app/theme.css", "", head, ""), "<p>x</p>");

        Assert.Contains(head, html, StringComparison.Ordinal);
        // Last thing in <head> — after the adopter stylesheet, so it can override anything before it.
        Assert.True(
            html.IndexOf("/app/theme.css", StringComparison.Ordinal) < html.IndexOf(head, StringComparison.Ordinal),
            "HeadHtml must be emitted after the custom stylesheet link");
        Assert.Contains(head + "</head>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_EmitsBodyStartHtmlVerbatim_ImmediatelyAfterBodyOpens()
    {
        var html = DashboardHtml.Page(
            new DashboardChrome("Exceptions", "/exceptions", "", "", "", "<header><a href=\"/admin\">Back</a></header>"),
            "<p>x</p>");

        Assert.Contains("<body><header><a href=\"/admin\">Back</a></header><p>x</p></body>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_OmitsSlots_WhenNotConfigured()
    {
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", ""), "<p>x</p>");

        Assert.Contains("<body><p>x</p></body>", html, StringComparison.Ordinal);
    }
}
