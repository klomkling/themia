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
    public void List_EncodesMessage_NoRawScript()
    {
        var items = new List<ExceptionEntry> { Entry("<script>alert(1)</script>") };
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };

        var html = DashboardHtml.List("Exceptions", "/exceptions", items, total: 1, filter);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public void List_LinksToDetailByGuid()
    {
        var e = Entry("boom");
        var html = DashboardHtml.List("Exceptions", "/exceptions", new List<ExceptionEntry> { e }, 1, new ExceptionFilter());

        Assert.Contains($"/exceptions/{e.Guid}", html);
    }

    [Fact]
    public void Detail_EncodesRequestBody_AndRespectsShowFlag()
    {
        var e = Entry("boom");
        e.RequestBody = "<script>steal()</script>";

        var shown = DashboardHtml.Detail("Exceptions", "/exceptions", e, showRequestBody: true, showRequestContext: false);
        Assert.Contains("&lt;script&gt;steal()&lt;/script&gt;", shown);
        Assert.DoesNotContain("<script>steal()</script>", shown);

        var hidden = DashboardHtml.Detail("Exceptions", "/exceptions", e, showRequestBody: false, showRequestContext: false);
        Assert.DoesNotContain("steal()", hidden);
    }
}
