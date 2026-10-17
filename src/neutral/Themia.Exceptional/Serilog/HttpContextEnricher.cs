using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Themia.Exceptional.Serilog;

/// <summary>
/// Adds the non-identifying request fields (Url/HttpMethod/Host/StatusCode) to log events. It adds nothing
/// that identifies the caller or carries request data: no <c>IpAddress</c>, <c>RequestBody</c> or
/// <c>RequestContext</c>. <see cref="ExceptionalSerilogSink"/> reads those from the request itself when it
/// persists an exception, so they never ride on routine log lines into the console, file or aggregator sinks.
/// </summary>
/// <remarks>
/// The captured <c>Url</c> omits the query string by default, since query parameters commonly carry
/// secrets (e.g. <c>?token=</c>, <c>?api_key=</c>). Set <see cref="ExceptionalOptions.CaptureQueryString"/>
/// to include it.
/// </remarks>
public sealed class HttpContextEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor accessor;
    private readonly ExceptionalOptions options;

    /// <summary>Creates the enricher.</summary>
    public HttpContextEnricher(IHttpContextAccessor accessor, ExceptionalOptions options)
    {
        this.accessor = accessor;
        this.options = options;
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var http = accessor.HttpContext;
        if (http is null)
            return;

        var request = http.Request;
        Add(logEvent, propertyFactory, "HttpMethod", request.Method);
        Add(logEvent, propertyFactory, "Host", request.Host.Value);
        var query = options.CaptureQueryString ? request.QueryString.Value : null;
        Add(logEvent, propertyFactory, "Url", $"{request.Scheme}://{request.Host}{request.Path}{query}");
        // Capture status when it has been set (even before the response body starts streaming). The
        // neutral enricher can only see the status the host set by log time; mapping typed exceptions
        // → status is the middleware's job.
        if (http.Response.HasStarted || http.Response.StatusCode != StatusCodes.Status200OK)
            Add(logEvent, propertyFactory, "StatusCode", http.Response.StatusCode);
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, object? value)
    {
        if (value is null or "")
            return;
        logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value));
    }
}
