using Microsoft.AspNetCore.Http;

namespace Themia.Audit.AspNetCore;

/// <summary>Configuration for the mountable, read-only audit dashboard.</summary>
public sealed class AuditDashboardOptions
{
    /// <summary>Gate run for every dashboard request. When <c>null</c>, all requests are denied
    /// (fail-closed) — the dashboard cannot be served without an explicit predicate.
    /// <para><strong>This predicate cannot scope the query.</strong> It returns <c>bool</c> — it can only
    /// allow or deny the whole request, never narrow which rows a request may see. A viewer authorized
    /// for tenant A can still read tenant B's events by editing the <c>?tenant=</c> query string, because
    /// the parsed <see cref="AuditQuery.TenantId"/> comes straight from the request. Use
    /// <see cref="ScopeQuery"/> — run after this predicate allows the request — to rewrite the query
    /// instead.</para></summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Runs after <see cref="Authorize"/> allows a request, to rewrite the parsed
    /// <see cref="AuditQuery"/> before it reaches <see cref="IAuditStore.QueryAsync"/> — the dashboard's
    /// tenant-scoping hook. The dashboard does not resolve a tenant from <c>ITenantContext</c> itself (it
    /// is mounted by the host, and a host admin viewing every tenant and a tenant admin viewing only their
    /// own are both legitimate), so an adopter who must confine a viewer to one tenant does it here:
    /// <code>
    /// options.ScopeQuery = (ctx, query) => query with { TenantId = ctx.User.FindFirst("tenant")?.Value };
    /// </code>
    /// The rewrite runs after the query string is parsed, so anything this hook sets on the returned
    /// <see cref="AuditQuery"/> is the one actually executed — a caller-supplied <c>?tenant=</c> (or any
    /// other filter) cannot override it. <c>null</c> (the default) keeps today's behaviour: the query
    /// runs exactly as parsed from the request, unfiltered by tenant unless the caller happens to supply
    /// one.
    /// <para>Also applied to the detail route, which carries no query string of its own to rewrite: this
    /// hook is invoked there against an empty <see cref="AuditQuery"/>, and the fetched row must satisfy
    /// every filter the hook sets (e.g. its <see cref="AuditQuery.TenantId"/>) or the route responds 404 —
    /// the same route-hiding response as an unknown <c>event_uid</c>. Without this, a viewer scoped to one
    /// tenant on the list route could still read any other tenant's row by requesting its
    /// <c>event_uid</c> directly.</para></summary>
    public Func<HttpContext, AuditQuery, AuditQuery>? ScopeQuery { get; set; }

    /// <summary>Runs when <see cref="Authorize"/> denies a request, instead of returning the bare 404.
    /// Use it to bounce an expired session to the host app's login page — otherwise a timed-out admin lands
    /// on a blank 404 with no explanation:
    /// <code>
    /// options.OnDenied = ctx =>
    /// {
    ///     ctx.Response.Redirect($"/login?returnUrl={UrlEncoder.Default.Encode(ctx.Request.Path)}");
    ///     return Task.CompletedTask;
    /// };
    /// </code>
    /// The hook owns the whole response. <c>null</c> (the default) keeps the route-hiding 404, so this is
    /// strictly opt-in. If it throws, the request still fails closed with the 404 — a broken hook can never
    /// serve the dashboard. Not called for a genuine not-found (an unknown event uid), only for a denial.
    /// <para><strong>Careful:</strong> a redirect reveals that the route exists, which the default 404
    /// deliberately hides. That is usually the right trade for an admin-facing dashboard, but it is your
    /// call to make.</para></summary>
    public Func<HttpContext, Task>? OnDenied { get; set; }

    /// <summary>Rows per page when the request omits <c>pageSize</c>. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Hard upper bound on rows per page (clamps the <c>pageSize</c> query param). Default 200.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>Document title (the browser tab). Also used as the list page's <c>&lt;h1&gt;</c> unless
    /// <see cref="Heading"/> is set. Default "Audit Log".</summary>
    public string Title { get; set; } = "Audit Log";

    /// <summary>The list page's <c>&lt;h1&gt;</c>. Empty (the default) falls back to <see cref="Title"/>.
    /// Set it when an adopter's own header bar (injected via <see cref="BodyStartHtml"/>) already carries
    /// the branding: <c>Title = "Contoso Audit Log"</c> keeps the browser tab unambiguous across apps while
    /// <c>Heading = "Audit Log"</c> stops the page restating what the bar above it already says.</summary>
    public string Heading { get; set; } = "";

    /// <summary>Optional URL to an extra stylesheet, injected into the dashboard <c>&lt;head&gt;</c> after
    /// the built-in CSS so its rules override the defaults — lets the dashboard match the host app.
    /// A relative URL is resolved against the dashboard mount path (so it loads on both the list and detail
    /// routes); root-relative (<c>/css/…</c>) and absolute URLs are used as-is. Empty (the default) emits no
    /// extra link.</summary>
    public string CustomStyleSheet { get; set; } = "";

    /// <summary>Optional URL to a favicon for the dashboard page. Resolved like <see cref="CustomStyleSheet"/>
    /// (relative → mount path; root-relative/absolute → as-is). Empty (the default) emits no icon link.</summary>
    public string CustomFavicon { get; set; } = "";

    /// <summary>Raw HTML emitted verbatim at the end of the dashboard <c>&lt;head&gt;</c> — after the built-in
    /// CSS and <see cref="CustomStyleSheet"/>, so it can override both. Use it for chrome the stylesheet
    /// hooks cannot express: a <c>&lt;meta name="viewport"&gt;</c>, an external script, extra links.
    /// Empty (the default) emits nothing.
    /// <para><strong>Not encoded</strong> — this is a trusted, adopter-authored slot; never build it from
    /// user input.</para>
    /// <para><strong>Use root-relative (<c>/app/x.js</c>) or absolute URLs.</strong> Unlike
    /// <see cref="CustomStyleSheet"/>/<see cref="CustomFavicon"/>, the markup is emitted verbatim and its
    /// URLs are not resolved against the mount path — and the page carries no <c>&lt;base&gt;</c>, so a
    /// page-relative URL resolves differently on the list (<c>/mount</c>) and detail (<c>/mount/{guid}</c>)
    /// routes.</para></summary>
    public string HeadHtml { get; set; } = "";

    /// <summary>Raw HTML emitted verbatim immediately after <c>&lt;body&gt;</c> opens, before the dashboard's
    /// own content. Use it for a header bar, a back-link to the host app, or a theme toggle. Empty (the
    /// default) emits nothing. <strong>Not encoded</strong>, and URLs are <strong>not</strong> resolved
    /// against the mount path — same trust and URL rules as <see cref="HeadHtml"/>.</summary>
    public string BodyStartHtml { get; set; } = "";

    /// <summary>
    /// Whether the detail view renders <see cref="AuditEntry.Data"/>. Default <c>false</c> — the opposite
    /// of <c>ExceptionalDashboardOptions.ShowRequestBody</c>, which defaults to <c>true</c>. The asymmetry is
    /// deliberate: <see cref="AuditEntry.Data"/> is an adopter-supplied payload, and redaction filters it by
    /// field <em>name</em> against a deny-list — a secret sitting in a field the deny-list does not name
    /// survives redaction unchanged and would reach this page. Off unless an adopter makes an explicit,
    /// informed decision to turn it on; still gated behind <see cref="Authorize"/> either way.
    /// </summary>
    public bool ShowData { get; set; }
}
