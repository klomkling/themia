using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Themia.Exceptional;
using Themia.Exceptional.Middleware;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

// The caller address, request body and request context are read by the sink from the live request while it
// stores an exception. They are not log-event properties, so no other sink on the logger sees them.
public sealed class ExceptionalSerilogSinkRequestContextTests
{
    private sealed class CapturingStore : IExceptionStore
    {
        public ExceptionEntry? Last;
        public Task LogAsync(ExceptionEntry e, CancellationToken ct = default) { Last = e; return Task.CompletedTask; }
        public Task<ExceptionEntry?> GetAsync(Guid g, CancellationToken ct = default) => Task.FromResult<ExceptionEntry?>(null);
        public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(new PagedResult<ExceptionEntry>());
        public Task<int> CountAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> ProtectAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HardDeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> PurgeAsync(DateTime o, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static HttpContext NewRequest()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        http.Request.Method = "GET";
        http.Request.Headers["User-Agent"] = "Edge";
        http.Request.Headers["Cf-Connecting-Ip"] = "203.0.113.9";
        http.Request.Headers["Authorization"] = "Bearer secret";
        return http;
    }

    private static (ILogger logger, CapturingStore store, RecordingSink other) Build(HttpContext? http, ExceptionalOptions options)
    {
        var store = new CapturingStore();
        var other = new RecordingSink();
        var accessor = new HttpContextAccessor { HttpContext = http };
        var logger = new LoggerConfiguration()
            .Enrich.With(new HttpContextEnricher(accessor, options))
            .WriteTo.Sink(other)
            .WriteTo.Sink(new ExceptionalSerilogSink(store, options, accessor))
            .CreateLogger();
        return (logger, store, other);
    }

    [Fact]
    public void RoutineLogLine_CarriesNoCallerIpAndNoRequestContext()
    {
        var options = new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = true };
        var (logger, _, other) = Build(NewRequest(), options);

        logger.Information("HTTP GET / responded 200");

        var evt = Assert.Single(other.Events);
        Assert.False(evt.Properties.ContainsKey("IpAddress"));
        Assert.False(evt.Properties.ContainsKey("RequestContext"));
        Assert.False(evt.Properties.ContainsKey("RequestBody"));
    }

    [Fact]
    public void ErrorLogLine_ReachingOtherSinks_AlsoCarriesNoCallerIpAndNoRequestContext()
    {
        var options = new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = true };
        var (logger, _, other) = Build(NewRequest(), options);

        logger.Error(new InvalidOperationException("boom"), "err");

        var evt = Assert.Single(other.Events);
        Assert.False(evt.Properties.ContainsKey("IpAddress"));
        Assert.False(evt.Properties.ContainsKey("RequestContext"));
    }

    [Fact]
    public void StoredException_GetsCallerIp_FromTheRequest()
    {
        var (logger, store, _) = Build(NewRequest(), new ExceptionalOptions { ApplicationName = "t" });

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Equal("203.0.113.9", store.Last!.IpAddress);
    }

    [Fact]
    public void StoredException_GetsRequestContext_Redacted_WhenEnabled()
    {
        var options = new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = true };
        var (logger, store, _) = Build(NewRequest(), options);

        logger.Error(new InvalidOperationException("boom"), "err");

        using var doc = JsonDocument.Parse(store.Last!.RequestContext!);
        var headers = doc.RootElement.GetProperty("headers");
        Assert.Equal("Edge", headers.GetProperty("User-Agent").GetString());
        Assert.Equal("***", headers.GetProperty("Authorization").GetString());
    }

    [Fact]
    public void StoredException_DropsEntry_WhenRedactorReturnsNull()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["drop-me"] = "x";
        http.Request.Headers["keep-me"] = "y";
        var options = new ExceptionalOptions
        {
            ApplicationName = "t",
            CaptureRequestContext = true,
            Redactor = (key, value) => key == "drop-me" ? null : value,
        };
        var (logger, store, _) = Build(http, options);

        logger.Error(new InvalidOperationException("boom"), "err");

        using var doc = JsonDocument.Parse(store.Last!.RequestContext!);
        var headers = doc.RootElement.GetProperty("headers");
        Assert.False(headers.TryGetProperty("drop-me", out _));
        Assert.Equal("y", headers.GetProperty("keep-me").GetString());
    }

    [Fact]
    public void StoredException_HasNoRequestContext_WhenDisabled()
    {
        var (logger, store, _) = Build(NewRequest(), new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = false });

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Null(store.Last!.RequestContext);
    }

    [Fact]
    public void StoredException_GetsRequestBody_WhenPresentInItems()
    {
        var http = NewRequest();
        http.Items[RequestBodyLoggingMiddleware.BodyItemKey] = "{\"key\":\"value\"}";
        var (logger, store, other) = Build(http, new ExceptionalOptions { ApplicationName = "t" });

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Equal("{\"key\":\"value\"}", store.Last!.RequestBody);
        Assert.False(Assert.Single(other.Events).Properties.ContainsKey("RequestBody"));
    }

    [Fact]
    public void StoredException_StillStores_WhenThereIsNoCurrentRequest()
    {
        var (logger, store, _) = Build(null, new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = true });

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.NotNull(store.Last);
        Assert.Null(store.Last!.IpAddress);
        Assert.Null(store.Last.RequestContext);
    }

    [Fact]
    public void StoredException_StillStores_WhenTheRedactorThrows()
    {
        var options = new ExceptionalOptions
        {
            ApplicationName = "t",
            CaptureRequestContext = true,
            Redactor = (_, _) => throw new InvalidOperationException("redactor bug"),
        };
        var (logger, store, _) = Build(NewRequest(), options);

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.NotNull(store.Last);
        Assert.Null(store.Last!.RequestContext);
        Assert.Equal("203.0.113.9", store.Last.IpAddress);
    }
}
