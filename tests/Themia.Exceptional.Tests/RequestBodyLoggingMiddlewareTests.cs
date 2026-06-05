using System.Text;
using Microsoft.AspNetCore.Http;
using Themia.Exceptional;
using Themia.Exceptional.Middleware;
using Xunit;

namespace Themia.Exceptional.Tests;

public class RequestBodyLoggingMiddlewareTests
{
    private static DefaultHttpContext ContextWithBody(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = body.Length;
        return ctx;
    }

    [Fact]
    public async Task Invoke_DoesNothing_WhenDisabled()
    {
        var options = new ExceptionalOptions { ApplicationName = "App", CaptureRequestBody = false };
        var ctx = ContextWithBody("hello");
        var mw = new RequestBodyLoggingMiddleware(_ => Task.CompletedTask, options);

        await mw.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestBodyLoggingMiddleware.BodyItemKey));
    }

    [Fact]
    public async Task Invoke_CapturesBody_WhenEnabled()
    {
        var options = new ExceptionalOptions { ApplicationName = "App", CaptureRequestBody = true };
        var ctx = ContextWithBody("hello world");
        var mw = new RequestBodyLoggingMiddleware(_ => Task.CompletedTask, options);

        await mw.InvokeAsync(ctx);

        Assert.Equal("hello world", ctx.Items[RequestBodyLoggingMiddleware.BodyItemKey]);
    }

    [Fact]
    public async Task Invoke_TruncatesToMaxBytes()
    {
        var options = new ExceptionalOptions { ApplicationName = "App", CaptureRequestBody = true, MaxBodyBytes = 4 };
        var ctx = ContextWithBody("hello world");
        var mw = new RequestBodyLoggingMiddleware(_ => Task.CompletedTask, options);

        await mw.InvokeAsync(ctx);

        Assert.Equal("hell…[truncated]", ctx.Items[RequestBodyLoggingMiddleware.BodyItemKey]);
    }

    [Fact]
    public async Task Invoke_LeavesBodyReadableDownstream()
    {
        var options = new ExceptionalOptions { ApplicationName = "App", CaptureRequestBody = true };
        var ctx = ContextWithBody("downstream");
        string? seen = null;
        var mw = new RequestBodyLoggingMiddleware(async c =>
        {
            using var reader = new StreamReader(c.Request.Body);
            seen = await reader.ReadToEndAsync();
        }, options);

        await mw.InvokeAsync(ctx);

        Assert.Equal("downstream", seen);
    }
}
