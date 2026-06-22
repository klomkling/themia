using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Serilog.Core;
using Serilog.Events;
using Themia.Exceptional.Middleware;

namespace Themia.Exceptional.Serilog;

/// <summary>
/// Adds HTTP request context (Url/HttpMethod/Host/IpAddress/StatusCode/RequestBody) to log events.
/// The base capture never reads Cookie/Authorization. When <see cref="ExceptionalOptions.CaptureRequestContext"/>
/// is enabled it additionally captures headers/cookies/query/form/server variables — each value run
/// through <see cref="ExceptionalOptions.Redactor"/>, whose default masks Authorization/Cookie and
/// secret-named values so live tokens are not stored.
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
        Add(logEvent, propertyFactory, "IpAddress", http.Connection.RemoteIpAddress?.ToString());
        // Capture status when it has been set (even before the response body starts streaming). The
        // neutral enricher can only see the status the host set by log time; mapping typed exceptions
        // → status is the middleware's job.
        if (http.Response.HasStarted || http.Response.StatusCode != StatusCodes.Status200OK)
            Add(logEvent, propertyFactory, "StatusCode", http.Response.StatusCode);
        // Base capture never reads Cookie/Authorization; the opt-in RequestContext below captures them
        // (redacted via options.Redactor) only when CaptureRequestContext is set.
        if (http.Items.TryGetValue(RequestBodyLoggingMiddleware.BodyItemKey, out var body) && body is string bodyText)
            Add(logEvent, propertyFactory, "RequestBody", bodyText);
        if (options.CaptureRequestContext)
        {
            string? requestContext = null;
            try { requestContext = BuildRequestContext(http, options.Redactor); }
            catch (Exception ex)
            {
                // Logging path: never throw. A buggy custom Redactor or serialization edge must not drop
                // the error being logged — capture the failure to SelfLog and omit RequestContext only.
                global::Serilog.Debugging.SelfLog.WriteLine("Themia.Exceptional: RequestContext capture failed: {0}", ex);
            }
            Add(logEvent, propertyFactory, "RequestContext", requestContext);
        }
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, object? value)
    {
        if (value is null or "")
            return;
        logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value));
    }

    private static readonly JsonSerializerOptions ContextJson = new() { WriteIndented = false };

    private static string BuildRequestContext(HttpContext http, RequestContextRedactor? redactor)
    {
        var request = http.Request;
        var ctx = new Dictionary<string, Dictionary<string, string?>>
        {
            ["headers"] = Collect(request.Headers, redactor),
            ["cookies"] = Collect(request.Cookies.Select(c => new KeyValuePair<string, StringValues>(c.Key, c.Value)), redactor),
            ["queryString"] = Collect(request.Query, redactor),
            ["form"] = TryForm(request, redactor),
            ["serverVariables"] = ServerVariables(http, redactor),
        };
        return JsonSerializer.Serialize(ctx, ContextJson);
    }

    private static Dictionary<string, string?> Collect(
        IEnumerable<KeyValuePair<string, StringValues>> pairs, RequestContextRedactor? redactor)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, values) in pairs)
        {
            var raw = values.ToString();
            var stored = redactor is null ? raw : redactor(key, raw);
            if (stored is not null)
                map[key] = stored;
        }
        return map;
    }

    private static Dictionary<string, string?> TryForm(HttpRequest request, RequestContextRedactor? redactor)
    {
        // Only read an already-buffered form; never force-read/rewind the body from the logging path.
        if (!request.HasFormContentType)
            return new Dictionary<string, string?>();
        try { return Collect(request.Form, redactor); }
        catch { return new Dictionary<string, string?>(); }
    }

    private static Dictionary<string, string?> ServerVariables(HttpContext http, RequestContextRedactor? redactor)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REMOTE_ADDR"] = http.Connection.RemoteIpAddress?.ToString() ?? "",
            ["SERVER_NAME"] = http.Request.Host.Host,
            ["SERVER_PORT"] = http.Request.Host.Port?.ToString() ?? "",
            ["REQUEST_METHOD"] = http.Request.Method,
            ["SERVER_PROTOCOL"] = http.Request.Protocol,
        };
        return Collect(raw.Select(kv => new KeyValuePair<string, StringValues>(kv.Key, kv.Value)), redactor);
    }
}
