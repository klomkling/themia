using System.Globalization;
using System.Net;
using System.Text;

namespace Themia.Audit.AspNetCore;

// Page-level chrome shared by every dashboard page (title, mount path, the optional adopter assets, and
// the two raw-HTML injection slots).
internal readonly record struct DashboardChrome(
    string Title,
    string Path,
    string CustomStyleSheet,
    string CustomFavicon,
    string HeadHtml = "",
    string BodyStartHtml = "",
    string Heading = "")
{
    /// <summary>The list page's h1. Falls back to <see cref="Title"/>, which drives the document title.</summary>
    internal string EffectiveHeading => string.IsNullOrEmpty(Heading) ? Title : Heading;
}

/// <summary>Pure, self-contained HTML rendering for the audit dashboard. Every <em>string</em> value is
/// HTML-encoded via <see cref="Enc"/>; all attacker-influenceable fields (adopter-named event fields, the
/// mount path, <see cref="AuditEntry.Data"/>) are string-typed and pass through it. Non-string values
/// (Guid, enum, formatted dates) are emitted raw — their <c>ToString()</c> cannot produce HTML
/// metacharacters. When adding a new string value, route it through <see cref="Enc"/>.
/// The sole exceptions are <c>DashboardChrome.HeadHtml</c> and <c>DashboardChrome.BodyStartHtml</c>:
/// they are trusted adopter-authored markup and are emitted verbatim by design.</summary>
internal static class DashboardHtml
{
    internal static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Builds a pager link that carries every active filter, not just the page cursor.
    /// </summary>
    /// <remarks>
    /// Emitting only <c>?page=</c> and <c>?pageSize=</c> dropped the filters on every Prev/Next click:
    /// a viewer who filtered by actor and clicked Next landed on page two of the <b>unfiltered</b> list,
    /// while the "N total" they had just read was counted for the filtered one. The two disagreeing on
    /// the same screen is what makes it a correctness bug rather than a nuisance — the page looks like a
    /// continuation of the search and is not.
    /// <para>
    /// Values are URL-encoded for the query string and then HTML-encoded for the attribute; both are
    /// required, and neither substitutes for the other.
    /// </para>
    /// </remarks>
    private static string PageLink(string path, AuditQuery query, int page)
    {
        var sb = new StringBuilder(path).Append("?page=").Append(page)
            .Append("&pageSize=").Append(query.PageSize);

        Add(sb, "tenant", query.TenantId);
        if (query.HostLevelOnly) sb.Append("&hostOnly=true");
        Add(sb, "actor", query.ActorId);
        Add(sb, "entityType", query.EntityType);
        Add(sb, "entityId", query.EntityId);
        Add(sb, "category", query.Category?.ToString());
        Add(sb, "outcome", query.Outcome?.ToString());
        Add(sb, "from", query.From?.ToString("O", CultureInfo.InvariantCulture));
        Add(sb, "to", query.To?.ToString("O", CultureInfo.InvariantCulture));

        return Enc(sb.ToString());

        static void Add(StringBuilder sb, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }
        }
    }

    internal static string Page(DashboardChrome chrome, string body)
    {
        var sb = new StringBuilder();
        // Without the viewport meta a mobile browser lays the page out at ~980px and zooms out to fit,
        // making the dashboard unreadable on a phone. Emitted before HeadHtml so an adopter that wants a
        // different viewport policy can still override it (for duplicate viewport metas, the last wins).
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(Enc(chrome.Title))
          .Append("</title><link rel=\"stylesheet\" href=\"").Append(Enc(chrome.Path)).Append("/dashboard.css\">");
        if (!string.IsNullOrEmpty(chrome.CustomFavicon))
        {
            var mimeType = FaviconMimeType(chrome.CustomFavicon);
            sb.Append("<link rel=\"icon\" ");
            if (mimeType is not null)
            {
                sb.Append("type=\"").Append(mimeType).Append("\" ");
            }

            sb.Append("href=\"").Append(Enc(ResolveAsset(chrome.Path, chrome.CustomFavicon))).Append("\">");
        }

        // Injected after the built-in stylesheet so an adopter's rules override the defaults.
        if (!string.IsNullOrEmpty(chrome.CustomStyleSheet))
        {
            sb.Append("<link rel=\"stylesheet\" href=\"").Append(Enc(ResolveAsset(chrome.Path, chrome.CustomStyleSheet))).Append("\" type=\"text/css\">");
        }

        // Trusted adopter markup, emitted verbatim (see the type doc) — last in <head> so it overrides
        // everything above it, and first in <body> so the adopter's chrome frames the dashboard content.
        sb.Append(chrome.HeadHtml).Append("</head><body>").Append(chrome.BodyStartHtml)
          .Append(body).Append("</body></html>");
        return sb.ToString();
    }

    // Derived from the extension, not hardcoded: a browser uses the type hint to decide it can render the
    // icon at all, so an SVG served without image/svg+xml may be skipped and leave the page iconless.
    // Unknown extension => omit the attribute rather than guess wrong (the browser then sniffs).
    // Mirrors Themia.Exceptional.AspNetCore's copy; kept as a local duplicate because the two dashboards
    // share no assembly and a shared package for one switch expression would not pay for itself.
    private static string? FaviconMimeType(string url)
    {
        var path = url.Split('?', '#')[0];
        var dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        return path[(dot + 1)..].ToLowerInvariant() switch
        {
            "svg" => "image/svg+xml",
            "png" => "image/png",
            "ico" => "image/x-icon",
            "gif" => "image/gif",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => null,
        };
    }

    // A relative custom asset URL is resolved against the dashboard mount path (like the built-in
    // dashboard.css) so it loads identically on the list (/mount) and detail (/mount/{guid}) routes —
    // the page has no <base>, and page-relative URLs would otherwise resolve differently between the two.
    // Root-relative ("/…"), protocol-relative ("//…") and absolute ("scheme://…") URLs are used verbatim.
    private static string ResolveAsset(string path, string url) =>
        url.StartsWith('/') || url.Contains("://", StringComparison.Ordinal) ? url : path + "/" + url;

    private static string OutcomeClass(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "outcome-success",
        AuditOutcome.Failure => "outcome-failure",
        AuditOutcome.Denied => "outcome-denied",
        _ => "outcome",
    };

    internal static string List(DashboardChrome chrome, IReadOnlyList<AuditEntry> items, int total, AuditQuery query, DateTimeOffset utcNow)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(Enc(chrome.EffectiveHeading)).Append("</h1>");

        var last = items.Count > 0 ? Relative(items[0].OccurredAt, utcNow) : "—";
        sb.Append("<p class=\"summary\"><strong>").Append(total).Append(" events</strong> (last: ").Append(Enc(last)).Append(")</p>");

        sb.Append("<form class=\"filter\" method=\"get\" action=\"").Append(Enc(chrome.Path)).Append("\">")
          .Append("<input name=\"tenant\" value=\"").Append(Enc(query.TenantId)).Append("\" placeholder=\"tenant\"> ")
          .Append("<input name=\"actor\" value=\"").Append(Enc(query.ActorId)).Append("\" placeholder=\"actor\"> ")
          .Append("<input name=\"entityType\" value=\"").Append(Enc(query.EntityType)).Append("\" placeholder=\"entity type\"> ")
          .Append("<input name=\"entityId\" value=\"").Append(Enc(query.EntityId)).Append("\" placeholder=\"entity id\"> ")
          .Append("<input name=\"category\" value=\"").Append(Enc(query.Category?.ToString())).Append("\" placeholder=\"category\"> ")
          .Append("<input name=\"outcome\" value=\"").Append(Enc(query.Outcome?.ToString())).Append("\" placeholder=\"outcome\"> ")
          .Append("<button type=\"submit\">Filter</button></form>");

        // Classed table/pager: the markup is a styling contract for adopter stylesheets, which would
        // otherwise need positional selectors ("body > p:last-of-type") that break on any layout change.
        sb.Append("<table class=\"events\"><thead><tr><th>Occurred</th><th>Category</th><th>Event</th><th>Outcome</th><th>Actor</th><th>Entity</th><th>Tenant</th></tr></thead><tbody>");
        foreach (var e in items)
        {
            sb.Append("<tr class=\"audit-row\">")
              .Append("<td><time title=\"").Append(Enc(e.OccurredAt.ToString("u", CultureInfo.InvariantCulture))).Append("\">")
              .Append(Enc(Relative(e.OccurredAt, utcNow))).Append("</time></td>")
              .Append("<td>").Append(Enc(e.Category.ToString())).Append("</td>")
              .Append("<td><a href=\"").Append(Enc(chrome.Path)).Append('/').Append(e.EventUid).Append("\">").Append(Enc(e.EventType)).Append("</a></td>")
              .Append("<td class=\"outcome ").Append(OutcomeClass(e.Outcome)).Append("\">").Append(Enc(e.Outcome.ToString())).Append("</td>")
              .Append("<td>").Append(Enc(e.ActorName ?? e.ActorId)).Append("</td>")
              .Append("<td>").Append(Enc(JoinEntity(e.EntityType, e.EntityId))).Append("</td>")
              .Append("<td>").Append(Enc(e.TenantId)).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</tbody></table>");

        var hasPrev = query.Page > 1;
        var hasNext = (long)query.Page * query.PageSize < total;
        sb.Append("<nav class=\"pager\">");
        if (hasPrev)
        {
            sb.Append("<a href=\"").Append(PageLink(chrome.Path, query, query.Page - 1)).Append("\">Prev</a> ");
        }
        sb.Append("Page ").Append(query.Page).Append(" (").Append(total).Append(" total) ");
        if (hasNext)
        {
            sb.Append("<a href=\"").Append(PageLink(chrome.Path, query, query.Page + 1)).Append("\">Next</a>");
        }
        sb.Append("</nav>");

        return Page(chrome, sb.ToString());
    }

    private static string? JoinEntity(string? entityType, string? entityId) =>
        entityType is null && entityId is null ? null : $"{entityType}#{entityId}";

    private static string Relative(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        var span = now - occurredAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds} secs ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} mins ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hours ago";
        return $"{(int)span.TotalDays} days ago";
    }

    internal static string Detail(DashboardChrome chrome, AuditEntry e, bool showData)
    {
        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Enc(chrome.Path)).Append("\">&larr; back</a></p>");
        sb.Append("<h1 class=\"outcome ").Append(OutcomeClass(e.Outcome)).Append("\">").Append(Enc(e.EventType)).Append("</h1>");

        sb.Append("<table class=\"meta\">");
        Row(sb, "Event uid", e.EventUid.ToString());
        Row(sb, "Category", e.Category.ToString());
        Row(sb, "Outcome", e.Outcome.ToString());
        Row(sb, "Tenant", e.TenantId);
        Row(sb, "Actor id", e.ActorId);
        Row(sb, "Actor name", e.ActorName);
        Row(sb, "Entity type", e.EntityType);
        Row(sb, "Entity id", e.EntityId);
        Row(sb, "Occurred", e.OccurredAt.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "IP", e.IpAddress);
        Row(sb, "User agent", e.UserAgent);
        Row(sb, "Correlation id", e.CorrelationId);
        Row(sb, "Reason", e.Reason);
        sb.Append("</table>");

        // ShowData defaults to false (AuditDashboardOptions.ShowData) — redaction filters Data by field
        // name, so a secret in an unnamed field would otherwise reach this page unredacted.
        if (showData && e.Data is not null)
        {
            sb.Append("<h2>Data</h2><pre>").Append(Enc(e.Data)).Append("</pre>");
        }

        return Page(chrome, sb.ToString());
    }

    private static void Row(StringBuilder sb, string key, string? value) =>
        sb.Append("<tr><th>").Append(Enc(key)).Append("</th><td>").Append(Enc(value)).Append("</td></tr>");
}
