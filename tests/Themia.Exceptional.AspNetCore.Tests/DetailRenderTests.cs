using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DetailRenderTests
{
    private static ExceptionEntry Entry() => new()
    {
        Type = "System.InvalidOperationException",
        Message = "boom",
        Detail = "{\"Message\":\"boom\",\"Type\":\"System.InvalidOperationException\",\"StackTrace\":\"at A()\\n at B()\",\"Inner\":null,\"Data\":null}",
        RequestContext = "{\"headers\":{\"User-Agent\":\"<b>Edge</b>\"},\"cookies\":{},\"queryString\":{},\"form\":{},\"serverVariables\":{\"REMOTE_ADDR\":\"::1\"}}",
    };

    [Fact]
    public void Detail_RendersStackTraceWithLineBreaks_NotRawJson()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: true);

        Assert.Contains("at A()\n at B()", html);          // real newline from the parsed StackTrace
        Assert.DoesNotContain("\\\"StackTrace\\\"", html);  // not the raw escaped-JSON blob
    }

    [Fact]
    public void Detail_RendersRequestContextSections_Encoded()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: true);

        Assert.Contains("Request Headers", html);
        Assert.Contains("Server Variables", html);
        Assert.Contains("&lt;b&gt;Edge&lt;/b&gt;", html);   // header value HTML-encoded
        Assert.DoesNotContain("<b>Edge</b>", html);
    }

    [Fact]
    public void Detail_OmitsRequestContext_WhenDisabled()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: false);
        Assert.DoesNotContain("Request Headers", html);
    }

    [Fact]
    public void Detail_EncodesRequestContextKeys()
    {
        var entry = new ExceptionEntry
        {
            Type = "System.InvalidOperationException",
            Message = "boom",
            Detail = "{}",
            RequestContext = "{\"headers\":{\"<img src=x onerror=alert(1)>\":\"v\"},\"cookies\":{},\"queryString\":{},\"form\":{},\"serverVariables\":{}}",
        };

        var html = DashboardHtml.Detail("Exceptions", "/exceptions", entry, showRequestBody: true, showRequestContext: true);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html); // key HTML-encoded
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html); // raw key never emitted
    }

    [Fact]
    public void Detail_FallsBackOnMalformedDetailJson()
    {
        var entry = new ExceptionEntry
        {
            Type = "System.InvalidOperationException",
            Message = "boom",
            Detail = "not json {",
        };

        var html = DashboardHtml.Detail("Exceptions", "/exceptions", entry, showRequestBody: true, showRequestContext: true);

        Assert.Contains("<h2>Detail</h2>", html);   // raw text rendered under a Detail heading
        Assert.Contains("not json {", html);
    }
}
