using Microsoft.AspNetCore.Http;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Configuration for the mountable exceptions dashboard.</summary>
public sealed class ExceptionalDashboardOptions
{
    /// <summary>Gate run for every dashboard request. When <c>null</c>, all requests are denied
    /// (fail-closed) — the dashboard cannot be served without an explicit predicate.</summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Rows per page when the request omits <c>pageSize</c>. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Hard upper bound on rows per page (clamps the <c>pageSize</c> query param). Default 200.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>Page heading and document title. Default "Exceptions".</summary>
    public string Title { get; set; } = "Exceptions";

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
