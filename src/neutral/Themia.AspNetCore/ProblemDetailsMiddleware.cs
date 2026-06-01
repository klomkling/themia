using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Themia.AspNetCore.Exceptions;

namespace Themia.AspNetCore;

/// <summary>Catches <see cref="ThemiaException"/>s (and unhandled exceptions) and writes RFC-7807 responses.</summary>
public sealed class ProblemDetailsMiddleware(
    RequestDelegate next,
    ILogger<ProblemDetailsMiddleware> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Processes the request, translating thrown exceptions into problem responses.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        try
        {
            await next(context);
        }
        catch (Exception ex) when (context.Response.HasStarted)
        {
            // The response is already on the wire; we cannot rewrite status/headers. Log and rethrow.
            logger.LogError(ex, "Response already started; cannot write problem response for {Method} {Path} (TraceId: {TraceId})",
                context.Request.Method, context.Request.Path, traceId);
            throw;
        }
        catch (NotFoundException ex) { await Write(context, 404, "Not Found", ex, traceId, LogLevel.Warning); }
        catch (ConflictException ex) { await Write(context, 409, "Conflict", ex, traceId, LogLevel.Warning); }
        catch (ForbiddenException ex) { await Write(context, 403, "Forbidden", ex, traceId, LogLevel.Warning); }
        catch (UnauthorizedException ex) { await Write(context, 401, "Unauthorized", ex, traceId, LogLevel.Warning); }
        catch (ValidationException ex) { await WriteValidation(context, ex, traceId); }
        catch (ExternalServiceException ex) { await Write(context, 503, "Service unavailable", ex, traceId, LogLevel.Error); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path} (TraceId: {TraceId})",
                context.Request.Method, context.Request.Path, traceId);
            await WriteGeneric(context, 500, "Server error", "An unexpected error occurred.", traceId);
        }
    }

    private async Task Write(HttpContext ctx, int status, string title, ThemiaException ex, string traceId, LogLevel level)
    {
        logger.Log(level, ex, "{Title} for {Method} {Path} (TraceId: {TraceId})",
            title, ctx.Request.Method, ctx.Request.Path, traceId);

        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;
        if (ex.Metadata is not null)
            foreach (var pair in ex.Metadata)
                problem.Extensions[pair.Key] = pair.Value;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }

    private async Task WriteValidation(HttpContext ctx, ValidationException ex, string traceId)
    {
        logger.LogWarning(ex, "Validation error for {Method} {Path} (TraceId: {TraceId})",
            ctx.Request.Method, ctx.Request.Path, traceId);

        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = 400;

        var problem = new ValidationProblemDetails(
            new Dictionary<string, string[]> { [ex.PropertyName] = [ex.Message] })
        {
            Status = 400,
            Title = "Validation error",
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;
        if (ex.Metadata is not null)
            foreach (var pair in ex.Metadata) problem.Extensions[pair.Key] = pair.Value;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }

    private async Task WriteGeneric(HttpContext ctx, int status, string title, string detail, string traceId)
    {
        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = ctx.Request.Path };
        problem.Extensions["traceId"] = traceId;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }
}
