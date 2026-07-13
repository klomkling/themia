using Microsoft.AspNetCore.Http;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Configuration for the mountable exceptions dashboard.</summary>
public sealed class ExceptionalDashboardOptions
{
    /// <summary>Gate run for every dashboard request. When <c>null</c>, all requests are denied
    /// (fail-closed) — the dashboard cannot be served without an explicit predicate.</summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

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
    /// serve the dashboard. Not called for a genuine not-found (an unknown exception id), only for a denial.
    /// <para><strong>Careful:</strong> a redirect reveals that the route exists, which the default 404
    /// deliberately hides. That is usually the right trade for an admin-facing dashboard, but it is your
    /// call to make.</para></summary>
    public Func<HttpContext, Task>? OnDenied { get; set; }

    /// <summary>Rows per page when the request omits <c>pageSize</c>. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Hard upper bound on rows per page (clamps the <c>pageSize</c> query param). Default 200.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>Document title (the browser tab). Also used as the list page's <c>&lt;h1&gt;</c> unless
    /// <see cref="Heading"/> is set. Default "Exceptions".</summary>
    public string Title { get; set; } = "Exceptions";

    /// <summary>The list page's <c>&lt;h1&gt;</c>. Empty (the default) falls back to <see cref="Title"/>.
    /// Set it when an adopter's own header bar (injected via <see cref="BodyStartHtml"/>) already carries the
    /// branding: <c>Title = "Contoso Exceptions"</c> keeps the browser tab unambiguous across apps while
    /// <c>Heading = "Exceptions"</c> stops the page restating what the bar above it already says.</summary>
    public string Heading { get; set; } = "";

    /// <summary>Optional URL to an extra stylesheet, injected into the dashboard <c>&lt;head&gt;</c> after
    /// the built-in CSS so its rules override the defaults — lets the dashboard match the host app.
    /// A relative URL is resolved against the dashboard mount path (so it loads on both the list and detail
    /// routes); root-relative (<c>/css/…</c>) and absolute URLs are used as-is. Empty (the default) emits no
    /// extra link. Parity feature for the jobs dashboard's <c>ThemiaQuartzOptions.CustomStyleSheet</c>
    /// (which emits the link unconditionally; this one omits it when empty).</summary>
    public string CustomStyleSheet { get; set; } = "";

    /// <summary>Optional URL to a favicon for the dashboard page. Resolved like <see cref="CustomStyleSheet"/>
    /// (relative → mount path; root-relative/absolute → as-is). Empty (the default) emits no icon link.
    /// Parity feature for the jobs dashboard's <c>ThemiaQuartzOptions.CustomFavicon</c>.</summary>
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
    /// routes. (The jobs dashboard's slots differ here: its layout has a <c>&lt;base&gt;</c>, which re-bases
    /// relative URLs onto the dashboard root.)</para></summary>
    public string HeadHtml { get; set; } = "";

    /// <summary>Raw HTML emitted verbatim immediately after <c>&lt;body&gt;</c> opens, before the dashboard's
    /// own content. Use it for a header bar, a back-link to the host app, or a theme toggle. Empty (the
    /// default) emits nothing. <strong>Not encoded</strong>, and URLs are <strong>not</strong> resolved
    /// against the mount path — same trust and URL rules as <see cref="HeadHtml"/>.</summary>
    public string BodyStartHtml { get; set; } = "";

    /// <summary>Whether the detail view renders the captured request body. Default <c>true</c> (shown only
    /// behind <see cref="Authorize"/>). Request bodies can contain secrets/PII; prefer scrubbing them at
    /// capture time, and set this to <c>false</c> if even authorized viewers should not see them.</summary>
    public bool ShowRequestBody { get; set; } = true;

    /// <summary>Whether protect/delete actions (POST) are exposed in the UI and accepted. Default <c>true</c>.
    /// Still gated by <see cref="Authorize"/> and a same-origin double-submit token.</summary>
    public bool EnableActions { get; set; } = true;

    /// <summary>Whether the detail view renders the captured request-context sections (headers/cookies/
    /// query/form/server variables) when present. Default <c>true</c>.</summary>
    public bool ShowRequestContext { get; set; } = true;
}
