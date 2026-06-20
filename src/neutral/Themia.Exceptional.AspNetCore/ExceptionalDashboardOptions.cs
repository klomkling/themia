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

    /// <summary>Whether the detail view renders the captured request body. Default <c>true</c> (shown only
    /// behind <see cref="Authorize"/>). Request bodies can contain secrets/PII; prefer scrubbing them at
    /// capture time, and set this to <c>false</c> if even authorized viewers should not see them.</summary>
    public bool ShowRequestBody { get; set; } = true;
}
