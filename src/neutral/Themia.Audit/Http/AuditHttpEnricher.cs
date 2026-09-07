using Microsoft.AspNetCore.Http;

namespace Themia.Audit.Http;

/// <summary>
/// Fills <see cref="AuditEntry.IpAddress"/> and <see cref="AuditEntry.UserAgent"/> from the current HTTP
/// request. Never overwrites a value the caller already supplied. Outside a request — e.g. a Quartz
/// worker host with no <see cref="HttpContext"/> — both come back <see langword="null"/>, which is
/// correct, not an error (design §10f).
/// </summary>
public sealed class AuditHttpEnricher
{
    private readonly IHttpContextAccessor accessor;

    /// <summary>Creates the enricher over <paramref name="accessor"/>.</summary>
    public AuditHttpEnricher(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        this.accessor = accessor;
    }

    /// <summary>
    /// Returns a copy of <paramref name="entry"/> with <see cref="AuditEntry.IpAddress"/> and
    /// <see cref="AuditEntry.UserAgent"/> filled from the current request, when not already set on
    /// <paramref name="entry"/> and when a request is available.
    /// </summary>
    /// <param name="entry">The entry to enrich.</param>
    /// <returns>A copy of <paramref name="entry"/> with the caller-address fields filled where absent.</returns>
    public AuditEntry Enrich(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var http = accessor.HttpContext;
        if (http is null)
        {
            return entry;
        }

        var userAgentHeader = http.Request.Headers.UserAgent.ToString();
        var userAgent = string.IsNullOrEmpty(userAgentHeader) ? null : userAgentHeader;

        return entry with
        {
            IpAddress = entry.IpAddress ?? http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = entry.UserAgent ?? userAgent,
        };
    }
}
