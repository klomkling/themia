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

        var html = DashboardHtml.List("Exceptions", "/exceptions", items, total: 200, filter, now);

        Assert.Contains("200 errors", html);                       // summary header
        Assert.Contains("secs ago", html);                          // relative time
        Assert.Contains("<time", html);                             // absolute on hover via <time title=…>
    }
}
