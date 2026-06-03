using System.Diagnostics;
using System.Text.Json;
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
        catch (NotFoundException ex) { await WriteAsync(context, 404, "Not Found", ex, traceId, LogLevel.Warning); }
        catch (ConflictException ex) { await WriteAsync(context, 409, "Conflict", ex, traceId, LogLevel.Warning); }
        catch (ForbiddenException ex) { await WriteAsync(context, 403, "Forbidden", ex, traceId, LogLevel.Warning); }
        catch (UnauthorizedException ex) { await WriteAsync(context, 401, "Unauthorized", ex, traceId, LogLevel.Warning); }
        catch (ValidationException ex) { await WriteValidationAsync(context, ex, traceId); }
        catch (ExternalServiceException ex) { await WriteAsync(context, 503, "Service unavailable", ex, traceId, LogLevel.Error); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path} (TraceId: {TraceId})",
                context.Request.Method, context.Request.Path, traceId);
            await WriteGenericAsync(context, 500, "Server error", "An unexpected error occurred.", traceId);
        }
    }

    private async Task WriteAsync(HttpContext ctx, int status, string title, ThemiaException ex, string traceId, LogLevel level)
    {
        logger.Log(level, ex, "{Title} for {Method} {Path} (TraceId: {TraceId})",
            title, ctx.Request.Method, ctx.Request.Path, traceId);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        // Consumer metadata first, then reserved keys last so they can't be overridden.
        AddMetadata(problem, ex.Metadata);
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;
        if (ex is ExternalServiceException ese) problem.Extensions["service"] = ese.ServiceName;

        await WriteProblemAsync(ctx, problem, status, traceId);
    }

    private async Task WriteValidationAsync(HttpContext ctx, ValidationException ex, string traceId)
    {
        logger.LogWarning(ex, "Validation error for {Method} {Path} (TraceId: {TraceId})",
            ctx.Request.Method, ctx.Request.Path, traceId);

        var problem = new ValidationProblemDetails(
            new Dictionary<string, string[]> { [ex.PropertyName] = [ex.Message] })
        {
            Status = 400,
            Title = "Validation error",
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        // Consumer metadata first, then reserved keys last so they can't be overridden.
        AddMetadata(problem, ex.Metadata);
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;

        await WriteProblemAsync(ctx, problem, 400, traceId);
    }

    private async Task WriteGenericAsync(HttpContext ctx, int status, string title, string detail, string traceId)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = ctx.Request.Path,
            Extensions = { ["traceId"] = traceId },
        };
        await WriteProblemAsync(ctx, problem, status, traceId);
    }

    // Serializes the body BEFORE touching the response. Consumer-supplied Metadata values are
    // arbitrary objects, so serialization can fail; if it does, we drop the extensions and emit a
    // minimal, guaranteed-serializable body rather than letting the throw escape this error handler.
    private async Task WriteProblemAsync(HttpContext ctx, ProblemDetails problem, int status, string traceId)
    {
        string payload;
        try
        {
            payload = JsonSerializer.Serialize(problem, problem.GetType(), Json);
        }
        catch (Exception serEx)
        {
            logger.LogError(serEx, "Failed to serialize problem response for {Method} {Path} (TraceId: {TraceId}); dropping extensions",
                ctx.Request.Method, ctx.Request.Path, traceId);
            problem.Extensions.Clear();
            problem.Extensions["traceId"] = traceId;
            payload = JsonSerializer.Serialize(problem, problem.GetType(), Json);
        }

        ResetResponse(ctx, status);
        await ctx.Response.WriteAsync(payload);
    }

    // Clears any Content-Length a downstream component may have set before throwing
    // (a stale value would truncate the problem body) and sets the problem content type/status.
    private static void ResetResponse(HttpContext ctx, int status)
    {
        ctx.Response.ContentLength = null;
        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;
    }

    private static void AddMetadata(ProblemDetails problem, IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null) return;
        foreach (var pair in metadata)
            problem.Extensions[pair.Key] = pair.Value;
    }
}
