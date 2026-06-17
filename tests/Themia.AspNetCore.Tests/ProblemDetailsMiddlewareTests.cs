using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class ProblemDetailsMiddlewareTests
{
    private static async Task<(int status, JsonElement body)> InvokeWith(Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();

        var mw = new ProblemDetailsMiddleware(
            _ => throw toThrow,
            NullLogger<ProblemDetailsMiddleware>.Instance);
        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    public static TheoryData<Func<ThemiaException>, int> DomainCases() => new()
    {
        { () => new NotFoundException("boom"), 404 },
        { () => new ConflictException("boom"), 409 },
        { () => new ForbiddenException("boom"), 403 },
        { () => new UnauthorizedException("boom"), 401 },
    };

    [Theory]
    [MemberData(nameof(DomainCases))]
    public async Task Maps_domain_exception_to_status(Func<ThemiaException> make, int expected)
    {
        var (status, body) = await InvokeWith(make());

        Assert.Equal(expected, status);
        Assert.Equal(expected, body.GetProperty("status").GetInt32());
        Assert.Equal("boom", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Write_branch_surfaces_errorCode_and_metadata()
    {
        var meta = new Dictionary<string, object?> { ["custom"] = "v" };
        var (status, body) = await InvokeWith(new ConflictException("dup", errorCode: "DUP", metadata: meta));

        Assert.Equal(409, status);
        Assert.Equal("DUP", body.GetProperty("errorCode").GetString());
        Assert.Equal("v", body.GetProperty("custom").GetString());
    }

    [Fact]
    public async Task ErrorCode_is_absent_when_not_set()
    {
        var (_, body) = await InvokeWith(new NotFoundException("x"));
        Assert.False(body.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task Validation_returns_400_with_field_and_errorCode()
    {
        var (status, body) = await InvokeWith(new ValidationException("Email", "bad", errorCode: "INVALID"));

        Assert.Equal(400, status);
        Assert.Equal("INVALID", body.GetProperty("errorCode").GetString());
        Assert.Equal("bad", body.GetProperty("errors").GetProperty("Email")[0].GetString());
    }

    [Fact]
    public async Task RateLimit_returns_429_with_RetryAfter_header_and_extension()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();
        var mw = new ProblemDetailsMiddleware(
            _ => throw new RateLimitException("slow down", retryAfterSeconds: 30, errorCode: "COOLDOWN"),
            NullLogger<ProblemDetailsMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("30", ctx.Response.Headers.RetryAfter);
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(await new StreamReader(ctx.Response.Body).ReadToEndAsync());
        Assert.Equal(30, doc.RootElement.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("COOLDOWN", doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ExternalService_returns_503_with_service_and_detail()
    {
        var (status, body) = await InvokeWith(new ExternalServiceException("payments", "down"));

        Assert.Equal(503, status);
        Assert.Equal("down", body.GetProperty("detail").GetString());
        Assert.Equal("payments", body.GetProperty("service").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Unknown_exception_returns_500_without_leaking_message()
    {
        var (status, body) = await InvokeWith(new InvalidOperationException("secret internals"));
        Assert.Equal(500, status);
        Assert.Equal("An unexpected error occurred.", body.GetProperty("detail").GetString());
        Assert.DoesNotContain("secret internals", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Non_serializable_metadata_degrades_instead_of_breaking()
    {
        var meta = new Dictionary<string, object?> { ["bad"] = new Cyclic() };
        var (status, body) = await InvokeWith(new NotFoundException("boom", metadata: meta));

        Assert.Equal(404, status);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Reserved_extensions_are_not_overwritten_by_metadata()
    {
        var meta = new Dictionary<string, object?> { ["traceId"] = "SPOOFED", ["custom"] = "keep" };
        var (_, body) = await InvokeWith(new NotFoundException("x", metadata: meta));

        Assert.NotEqual("SPOOFED", body.GetProperty("traceId").GetString());
        Assert.Equal("keep", body.GetProperty("custom").GetString());
    }

    [Fact]
    public async Task Clears_stale_content_length_before_writing()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();

        var mw = new ProblemDetailsMiddleware(
            c => { c.Response.ContentLength = 9999; throw new NotFoundException("boom"); },
            NullLogger<ProblemDetailsMiddleware>.Instance);
        await mw.InvokeAsync(ctx);

        Assert.NotEqual(9999L, ctx.Response.ContentLength);
    }

    [Fact]
    public async Task Rethrows_when_response_already_started()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        var mw = new ProblemDetailsMiddleware(
            _ => throw new NotFoundException("boom"),
            NullLogger<ProblemDetailsMiddleware>.Instance);

        await Assert.ThrowsAsync<NotFoundException>(() => mw.InvokeAsync(ctx));
    }

    [Fact]
    public async Task Client_aborted_cancellation_is_rethrown_not_turned_into_500()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();
        ctx.RequestAborted = new CancellationToken(canceled: true);

        var mw = new ProblemDetailsMiddleware(
            _ => throw new OperationCanceledException(),
            NullLogger<ProblemDetailsMiddleware>.Instance);

        // The client disconnected — cancellation, not a server error: propagate it, leaving the status
        // untouched and writing nothing to the dead connection (never a 500).
        await Assert.ThrowsAsync<OperationCanceledException>(() => mw.InvokeAsync(ctx));
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(0, ctx.Response.Body.Length);
    }

    [Fact]
    public async Task Client_aborted_TaskCanceledException_is_rethrown_not_turned_into_500()
    {
        // The real-world subtype: Kestrel/await on an aborted token throws TaskCanceledException (a subclass
        // of OperationCanceledException). The catch must cover it, not just the base type.
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();
        ctx.RequestAborted = new CancellationToken(canceled: true);

        var mw = new ProblemDetailsMiddleware(
            _ => throw new TaskCanceledException(),
            NullLogger<ProblemDetailsMiddleware>.Instance);

        await Assert.ThrowsAsync<TaskCanceledException>(() => mw.InvokeAsync(ctx));
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(0, ctx.Response.Body.Length);
    }

    [Fact]
    public async Task Client_aborted_cancellation_with_started_response_logs_as_cancellation_not_error()
    {
        // Even when the response has started, a client abort takes the cancellation path, NOT the
        // "response already started" error path — proving the OCE-abort catch is checked first. Both paths
        // rethrow without a 500, so the discriminating signal is the LOG: Debug "aborted", never Error.
        var logger = new CapturingLogger<ProblemDetailsMiddleware>();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        ctx.RequestAborted = new CancellationToken(canceled: true);

        var mw = new ProblemDetailsMiddleware(_ => throw new OperationCanceledException(), logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => mw.InvokeAsync(ctx));
        Assert.NotEqual(500, ctx.Response.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("aborted by the client"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_without_client_abort_still_returns_500()
    {
        // An OCE that is NOT a client abort (RequestAborted not signalled) stays a genuine failure on the
        // generic 500 path — only client-initiated cancellation is treated specially.
        var (status, _) = await InvokeWith(new OperationCanceledException());
        Assert.Equal(500, status);
    }

    /// <summary>A value that fails System.Text.Json serialization (self-referencing cycle).</summary>
    private sealed class Cyclic
    {
        public Cyclic Self => this;
    }

    /// <summary>Stub response feature whose <see cref="HasStarted"/> is always true.</summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public string? ReasonPhrase { get; set; }
        public int StatusCode { get; set; } = 200;
        public void OnCompleted(Func<object, Task> callback, object state) { }
        public void OnStarting(Func<object, Task> callback, object state) { }
    }

    /// <summary>Captures logged entries (level + formatted message) so tests can assert log severity.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
