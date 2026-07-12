using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

/// <summary>
/// Title drove both the document &lt;title&gt; and the list page &lt;h1&gt;. Once an adopter injects their own
/// header bar via BodyStartHtml, that &lt;h1&gt; restates branding the bar already carries — and shortening
/// Title to fix the heading makes the browser-tab title ambiguous across apps. Heading separates the two.
/// </summary>
public sealed class DashboardHeadingTests
{
    private static string Render(string title, string heading)
    {
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };
        return DashboardHtml.List(
            new DashboardChrome(title, "/exceptions", "", "", "", "", heading),
            new List<ExceptionEntry>(), total: 0, filter, DateTime.UtcNow);
    }

    [Fact]
    public void Heading_DefaultsToEmpty()
    {
        Assert.Equal(string.Empty, new ExceptionalDashboardOptions().Heading);
    }

    [Fact]
    public void List_FallsBackToTitle_WhenHeadingUnset()
    {
        // Unchanged behaviour for everyone who never sets Heading.
        var html = Render(title: "EzyAssets Exceptions", heading: "");

        Assert.Contains("<title>EzyAssets Exceptions</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>EzyAssets Exceptions</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void List_UsesHeadingForH1_AndTitleForDocumentTitle_WhenBothSet()
    {
        var html = Render(title: "EzyAssets Exceptions", heading: "Exceptions");

        Assert.Contains("<title>EzyAssets Exceptions</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Exceptions</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void List_EncodesHeading()
    {
        var html = Render(title: "Exceptions", heading: "<script>h()</script>");

        Assert.Contains("&lt;script&gt;h()&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>h()</script>", html, StringComparison.Ordinal);
    }
}
