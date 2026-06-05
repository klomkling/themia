using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Themia.Exceptional.Serilog;

/// <summary>
/// Adds HTTP request context (Url/HttpMethod/Host/IpAddress) to log events. Never reads Cookie/Authorization
/// (or configured scrub keys), so secrets cannot leak into stored exceptions.
/// </summary>
/// <remarks>
/// The captured <c>Url</c> includes <see cref="Microsoft.AspNetCore.Http.HttpRequest.QueryString"/> verbatim.
/// Query parameters may carry sensitive values (e.g. <c>?token=</c>, <c>?api_key=</c>). Query-param
/// redaction via a configurable <c>ScrubKeys</c> list is a planned follow-up; until then, callers should
/// be aware that stored URLs may expose query-string secrets.
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
        Add(logEvent, propertyFactory, "Url", $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}");
        Add(logEvent, propertyFactory, "IpAddress", http.Connection.RemoteIpAddress?.ToString());
        if (http.Response.HasStarted)
            Add(logEvent, propertyFactory, "StatusCode", http.Response.StatusCode);
        // Cookie/Authorization and options.ScrubKeys are intentionally never read.
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, object? value)
    {
        if (value is null or "")
            return;
        logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value));
    }
}
