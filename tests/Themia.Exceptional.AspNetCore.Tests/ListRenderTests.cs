using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class ListRenderTests
{
    [Fact]
    public void List_RendersSummaryAndRelativeTime()
    {
        var now = DateTime.UtcNow;
        var items = new List<ExceptionEntry>
        {
            new() { Guid = Guid.NewGuid(), Type = "System.Exception", Message = "m", ApplicationName = "App",
                    LastLogDate = now.AddSeconds(-14), DuplicateCount = 2 },
        };
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };

        var html = DashboardHtml.List(new DashboardChrome("Exceptions", "/exceptions", "", ""), items, total: 200, filter, now);

        Assert.Contains("200 errors", html);                       // summary header
        Assert.Contains("secs ago", html);                          // relative time
        Assert.Contains("<time", html);                             // absolute on hover via <time title=…>
    }

    // The list markup is a styling contract for adopter stylesheets: without stable hooks they can only
    // reach the table and the pager through fragile positional selectors ("body > p:last-of-type").
    [Fact]
    public void List_TableIsClassed_WithHeadAndBody()
    {
        var html = Render(page: 1, total: 200);

        Assert.Contains("<table class=\"errors\">", html, StringComparison.Ordinal);
        Assert.Contains("<thead>", html, StringComparison.Ordinal);
        Assert.Contains("<tbody>", html, StringComparison.Ordinal);
        Assert.Contains("</tbody></table>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void List_PagerIsClassedNav_WithPrevAndNextLinks()
    {
        var html = Render(page: 2, total: 200);

        Assert.Contains("<nav class=\"pager\">", html, StringComparison.Ordinal);
        Assert.Contains("</nav>", html, StringComparison.Ordinal);
        Assert.Contains(">Prev</a>", html, StringComparison.Ordinal);
        Assert.Contains(">Next</a>", html, StringComparison.Ordinal);
    }

    private static string Render(int page, int total)
    {
        var now = DateTime.UtcNow;
        var items = new List<ExceptionEntry>
        {
            new() { Guid = Guid.NewGuid(), Type = "System.Exception", Message = "m", ApplicationName = "App",
                    LastLogDate = now.AddSeconds(-14), DuplicateCount = 2 },
        };
        var filter = new ExceptionFilter { Page = page, PageSize = 50 };
        return DashboardHtml.List(new DashboardChrome("Exceptions", "/exceptions", "", ""), items, total, filter, now);
    }
}
