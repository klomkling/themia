using Microsoft.AspNetCore.Http;

namespace Themia.AI.AspNetCore;

/// <summary>Configuration for the mountable AI provider probe.</summary>
public sealed class AiProbeOptions
{
    /// <summary>Gate run for every probe request (both the configuration report and the call probe).
    /// When <c>null</c>, all requests are denied (fail-closed) — the probe cannot be served without an
    /// explicit predicate, matching <c>Themia.Audit.AspNetCore</c>'s
    /// <c>AuditDashboardOptions.Authorize</c>.</summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }
}
