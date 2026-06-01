using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    [Theory]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(ConflictException), 409)]
    [InlineData(typeof(ForbiddenException), 403)]
    [InlineData(typeof(UnauthorizedException), 401)]
    public async Task Maps_domain_exception_to_status(Type exType, int expected)
    {
        var ex = (Exception)Activator.CreateInstance(exType, "boom", null, null)!;
        var (status, body) = await InvokeWith(ex);

        Assert.Equal(expected, status);
        Assert.Equal(expected, body.GetProperty("status").GetInt32());
        Assert.Equal("boom", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
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
    public async Task ExternalService_returns_503()
    {
        var (status, _) = await InvokeWith(new ExternalServiceException("payments", "down"));
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task Unknown_exception_returns_500_without_leaking_message()
    {
        var (status, body) = await InvokeWith(new InvalidOperationException("secret internals"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("secret internals", body.GetProperty("detail").GetString());
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
}
