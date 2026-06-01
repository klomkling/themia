using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
        return (ctx.Response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
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
}
