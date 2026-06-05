using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Themia.Exceptional;
using Themia.Exceptional.Middleware;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public class HttpContextEnricherTests
{
    private static LogEvent NewEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Error, new InvalidOperationException("x"),
        new MessageTemplate("m", Array.Empty<MessageTemplateToken>()), Array.Empty<LogEventProperty>());

    private static (HttpContextEnricher enricher, LogEvent evt) Setup(Action<HttpContext> configure)
    {
        var http = new DefaultHttpContext();
        configure(http);
        var accessor = new HttpContextAccessor { HttpContext = http };
        var options = new ExceptionalOptions { ApplicationName = "App" };
        return (new HttpContextEnricher(accessor, options), NewEvent());
    }

    [Fact]
    public void Enrich_AddsRequestProperties()
    {
        var (enricher, evt) = Setup(http =>
        {
            http.Request.Method = "POST";
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("example.com");
            http.Request.Path = "/orders";
        });

        enricher.Enrich(evt, new LogEventPropertyFactory());

        Assert.Equal("POST", evt.Properties["HttpMethod"].ToString().Trim('"'));
        Assert.Contains("/orders", evt.Properties["Url"].ToString());
    }

    [Fact]
    public void Enrich_DoesNotLeakAuthorizationOrCookie()
    {
        var (enricher, evt) = Setup(http =>
        {
            http.Request.Headers.Authorization = "Bearer secret";
            http.Request.Headers.Cookie = "session=abc";
            http.Request.Method = "GET";
        });

        enricher.Enrich(evt, new LogEventPropertyFactory());

        var all = string.Join("|", evt.Properties.Values.Select(v => v.ToString()));
        Assert.DoesNotContain("secret", all);
        Assert.DoesNotContain("session=abc", all);
    }

    [Fact]
    public void Enrich_AddsRequestBody_WhenPresentInItems()
    {
        var (enricher, evt) = Setup(http =>
        {
            http.Request.Method = "POST";
            http.Items[RequestBodyLoggingMiddleware.BodyItemKey] = "{\"key\":\"value\"}";
        });

        enricher.Enrich(evt, new LogEventPropertyFactory());

        Assert.True(evt.Properties.TryGetValue("RequestBody", out var bodyProp));
        var raw = Assert.IsType<ScalarValue>(bodyProp).Value;
        Assert.Equal("{\"key\":\"value\"}", raw);
    }

    [Fact]
    public void Enrich_CapturesStatusCode_WhenSetButResponseNotStarted()
    {
        var (enricher, evt) = Setup(http =>
        {
            http.Request.Method = "GET";
            http.Response.StatusCode = 404;
        });

        enricher.Enrich(evt, new LogEventPropertyFactory());

        Assert.True(evt.Properties.TryGetValue("StatusCode", out var sc));
        Assert.Equal("404", sc.ToString());
    }

    [Fact]
    public void Enrich_DoesNotAddStatusCode_WhenResponseIs200AndNotStarted()
    {
        // DefaultHttpContext has StatusCode=200 and HasStarted=false — the enricher must suppress it.
        var (enricher, evt) = Setup(http =>
        {
            http.Request.Method = "GET";
            // StatusCode defaults to 200, HasStarted defaults to false — no changes needed.
        });

        enricher.Enrich(evt, new LogEventPropertyFactory());

        Assert.False(evt.Properties.ContainsKey("StatusCode"));
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenHttpContextIsNull()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var options = new ExceptionalOptions { ApplicationName = "App" };
        var enricher = new HttpContextEnricher(accessor, options);
        var evt = NewEvent();

        enricher.Enrich(evt, new LogEventPropertyFactory());

        Assert.Empty(evt.Properties);
    }
}

// Minimal property factory for unit testing enrichers.
file sealed class LogEventPropertyFactory : ILogEventPropertyFactory
{
    public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
        => new(name, new ScalarValue(value));
}
