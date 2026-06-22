using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Pure, self-contained HTML rendering for the exceptions dashboard. Every <em>string</em>
/// value is HTML-encoded via <see cref="Enc"/>; all attacker-influenceable fields (messages, request
/// bodies, URLs, the mount path) are string-typed and pass through it. Non-string values (Guid, int,
/// bool, formatted dates) are emitted raw — their <c>ToString()</c> cannot produce HTML metacharacters.
/// When adding a new string value, route it through <see cref="Enc"/>.</summary>
internal static class DashboardHtml
{
    internal static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    internal static string Page(string title, string path, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Enc(title) +
        "</title><link rel=\"stylesheet\" href=\"" + Enc(path) + "/dashboard.css\"></head><body>" + body + "</body></html>";

    internal static string List(string title, string path, IReadOnlyList<ExceptionEntry> items, int total, ExceptionFilter filter)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(Enc(title)).Append("</h1>");

        sb.Append("<form method=\"get\" action=\"").Append(Enc(path)).Append("\">")
          .Append("<input name=\"q\" value=\"").Append(Enc(filter.Search)).Append("\" placeholder=\"search\"> ")
          .Append("<input name=\"app\" value=\"").Append(Enc(filter.ApplicationName)).Append("\" placeholder=\"app\"> ")
          .Append("<input name=\"tenant\" value=\"").Append(Enc(filter.TenantId)).Append("\" placeholder=\"tenant\"> ")
          .Append("<button type=\"submit\">Filter</button></form>");

        sb.Append("<table><tr><th>Last log</th><th>App</th><th>Type</th><th>Message</th><th>Status</th><th>Count</th><th>Tenant</th></tr>");
        foreach (var e in items)
        {
            sb.Append("<tr>")
              .Append("<td>").Append(Enc(e.LastLogDate.ToString("u", CultureInfo.InvariantCulture))).Append("</td>")
              .Append("<td>").Append(Enc(e.ApplicationName)).Append("</td>")
              .Append("<td><a href=\"").Append(Enc(path)).Append('/').Append(e.Guid).Append("\">").Append(Enc(e.Type)).Append("</a></td>")
              .Append("<td>").Append(Enc(e.Message)).Append("</td>")
              .Append("<td>").Append(Enc(e.StatusCode?.ToString(CultureInfo.InvariantCulture))).Append("</td>")
              .Append("<td>").Append(e.DuplicateCount).Append("</td>")
              .Append("<td>").Append(Enc(e.TenantId)).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</table>");

        var hasPrev = filter.Page > 1;
        var hasNext = (long)filter.Page * filter.PageSize < total;
        sb.Append("<p>");
        if (hasPrev)
        {
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page - 1)
              .Append("&amp;pageSize=").Append(filter.PageSize).Append("\">Prev</a> ");
        }
        sb.Append("Page ").Append(filter.Page).Append(" (").Append(total).Append(" total) ");
        if (hasNext)
        {
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page + 1)
              .Append("&amp;pageSize=").Append(filter.PageSize).Append("\">Next</a>");
        }
        sb.Append("</p>");

        return Page(title, path, sb.ToString());
    }

    internal static string Detail(string title, string path, ExceptionEntry e, bool showRequestBody, bool showRequestContext)
    {
        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Enc(path)).Append("\">&larr; back</a></p>");
        sb.Append("<h1 class=\"type type-err\">").Append(Enc(e.Type)).Append("</h1>");
        sb.Append("<p>").Append(Enc(e.Message)).Append("</p>");

        sb.Append("<table class=\"meta\">");
        Row(sb, "Guid", e.Guid.ToString());
        Row(sb, "Application", e.ApplicationName);
        Row(sb, "Machine", e.MachineName);
        Row(sb, "Tenant", e.TenantId);
        Row(sb, "Status", e.StatusCode?.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Method", e.HttpMethod);
        Row(sb, "Url", e.Url);
        Row(sb, "Host", e.Host);
        Row(sb, "IP", e.IpAddress);
        Row(sb, "Source", e.Source);
        Row(sb, "Count", e.DuplicateCount.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Created", e.CreationDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Last log", e.LastLogDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Protected", e.IsProtected.ToString());
        sb.Append("</table>");

        // Parse the stored Detail JSON and render the stack trace with real line breaks (the old UI
        // dumped the whole escaped-JSON blob). Fall back to the raw text if it is not valid JSON.
        var (stackTrace, inner, data) = ParseDetail(e.Detail);
        if (stackTrace is not null)
        {
            sb.Append("<h2>Stack Trace</h2><pre>").Append(Enc(stackTrace)).Append("</pre>");
            if (!string.IsNullOrEmpty(inner))
                sb.Append("<h2>Inner Exception</h2><pre>").Append(Enc(inner)).Append("</pre>");
            if (!string.IsNullOrEmpty(data))
                sb.Append("<h2>Data</h2><pre>").Append(Enc(data)).Append("</pre>");
        }
        else
        {
            sb.Append("<h2>Detail</h2><pre>").Append(Enc(e.Detail)).Append("</pre>");
        }

        if (showRequestBody && e.RequestBody is not null)
            sb.Append("<h2>Request Body</h2><pre>").Append(Enc(e.RequestBody)).Append("</pre>");

        if (showRequestContext && e.RequestContext is not null)
            AppendRequestContext(sb, e.RequestContext);

        return Page(title, path, sb.ToString());
    }

    private static (string? StackTrace, string? Inner, string? Data) ParseDetail(string detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            var root = doc.RootElement;
            string? Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? data = root.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.Object ? d.GetRawText() : null;
            return (Get("StackTrace"), Get("Inner"), data);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static readonly (string Key, string Heading)[] ContextGroups =
    {
        ("serverVariables", "Server Variables"),
        ("requestHeaders", "Request Headers"), // tolerate either key spelling
        ("headers", "Request Headers"),
        ("cookies", "Cookies"),
        ("queryString", "QueryString"),
        ("form", "Form"),
    };

    private static void AppendRequestContext(StringBuilder sb, string requestContext)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(requestContext); }
        catch (JsonException) { return; }
        using (doc)
        {
            foreach (var (key, heading) in ContextGroups)
            {
                if (!doc.RootElement.TryGetProperty(key, out var group) || group.ValueKind != JsonValueKind.Object)
                    continue;
                var rows = new StringBuilder();
                foreach (var prop in group.EnumerateObject())
                    rows.Append("<tr><th>").Append(Enc(prop.Name)).Append("</th><td>")
                        .Append(Enc(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText()))
                        .Append("</td></tr>");
                if (rows.Length == 0)
                    continue;
                sb.Append("<h2>").Append(Enc(heading)).Append("</h2><table>").Append(rows).Append("</table>");
            }
        }
    }

    private static void Row(StringBuilder sb, string key, string? value) =>
        sb.Append("<tr><th>").Append(Enc(key)).Append("</th><td>").Append(Enc(value)).Append("</td></tr>");
}
