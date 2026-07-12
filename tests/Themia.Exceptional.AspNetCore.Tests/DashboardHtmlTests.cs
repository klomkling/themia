using System.Collections.Generic;
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public class DashboardHtmlTests
{
    private static ExceptionEntry Entry(string message) => new()
    {
        Guid = Guid.NewGuid(),
        ApplicationName = "app",
        Type = "System.Exception",
        Message = message,
        Detail = "{}",
        DuplicateCount = 1,
    };

    [Fact]
    public void Page_EmitsViewportMeta_ByDefault()
    {
        // Without it a mobile browser renders the dashboard at ~980px and zooms out — unreadable on a
        // phone. It is the same tag for every adopter, so it belongs in the default chrome, not in HeadHtml.
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", ""), "<p>x</p>");

        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_EmitsViewportMeta_BeforeHeadHtml_SoAdopterCanOverride()
    {
        const string custom = "<meta name=\"viewport\" content=\"width=1024\">";
        var html = DashboardHtml.Page(new DashboardChrome("Exceptions", "/exceptions", "", "", custom, ""), "<p>x</p>");

        // For duplicate viewport metas the last one wins, so the adopter's must come after the default.
        Assert.True(
            html.IndexOf("width=device-width", StringComparison.Ordinal) < html.IndexOf("width=1024", StringComparison.Ordinal),
            "the built-in viewport meta must precede HeadHtml so an adopter's own viewport overrides it");
    }

    [Fact]
    public void List_EncodesMessage_NoRawScript()
    {
        var items = new List<ExceptionEntry> { Entry("<script>alert(1)</script>") };
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };

        var html = DashboardHtml.List(new DashboardChrome("Exceptions", "/exceptions", "", ""), items, total: 1, filter, DateTime.UtcNow);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public void List_LinksToDetailByGuid()
    {
        var e = Entry("boom");
        var html = DashboardHtml.List(new DashboardChrome("Exceptions", "/exceptions", "", ""), new List<ExceptionEntry> { e }, 1, new ExceptionFilter(), DateTime.UtcNow);

        Assert.Contains($"/exceptions/{e.Guid}", html);
    }

    [Fact]
    public void Detail_EncodesRequestBody_AndRespectsShowFlag()
    {
        var e = Entry("boom");
        e.RequestBody = "<script>steal()</script>";

        var shown = DashboardHtml.Detail(new DashboardChrome("Exceptions", "/exceptions", "", ""), e, showRequestBody: true, showRequestContext: false);
        Assert.Contains("&lt;script&gt;steal()&lt;/script&gt;", shown);
        Assert.DoesNotContain("<script>steal()</script>", shown);

        var hidden = DashboardHtml.Detail(new DashboardChrome("Exceptions", "/exceptions", "", ""), e, showRequestBody: false, showRequestContext: false);
        Assert.DoesNotContain("steal()", hidden);
    }
}
