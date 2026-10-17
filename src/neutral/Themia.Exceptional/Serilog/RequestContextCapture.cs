using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Themia.Exceptional.Serilog;

/// <summary>Builds the redacted request-context JSON stored on an exception. Used by
/// <see cref="ExceptionalSerilogSink"/> only, so the context never travels as a log-event property to the
/// other sinks (console, files, aggregators).</summary>
internal static class RequestContextCapture
{
    private static readonly JsonSerializerOptions ContextJson = new() { WriteIndented = false };

    internal static string Build(HttpContext http, RequestContextRedactor? redactor)
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
        catch (Exception ex)
        {
            global::Serilog.Debugging.SelfLog.WriteLine("Themia.Exceptional: request form capture failed: {0}", ex);
            return new Dictionary<string, string?>();
        }
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
