namespace Themia.Exceptional;

/// <summary>A persisted exception occurrence (one row of the <c>Exceptions</c> table).</summary>
public sealed class ExceptionEntry
{
    /// <summary>Database identity key.</summary>
    public long Id { get; set; }

    /// <summary>Stable unique id for this stored error.</summary>
    public Guid Guid { get; set; }

    /// <summary>Logical application name.</summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Originating machine/host name.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Fully-qualified exception type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Exception <c>Source</c> (truncated).</summary>
    public string? Source { get; set; }

    /// <summary>Exception message (truncated).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Full serialized error payload (System.Text.Json).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Request host, when captured.</summary>
    public string? Host { get; set; }

    /// <summary>Request URL, when captured.</summary>
    public string? Url { get; set; }

    /// <summary>Request HTTP method, when captured.</summary>
    public string? HttpMethod { get; set; }

    /// <summary>Client IP address, when captured.</summary>
    public string? IpAddress { get; set; }

    /// <summary>HTTP status code, when captured.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Rollup key — deterministic across processes.</summary>
    public string ErrorHash { get; set; } = string.Empty;

    /// <summary>Number of occurrences rolled up into this row.</summary>
    public int DuplicateCount { get; set; } = 1;

    /// <summary>Owning tenant id, when a tenant context was present. The neutral core never filters by it.</summary>
    public string? TenantId { get; set; }

    /// <summary>First-seen timestamp (UTC).</summary>
    public DateTime CreationDate { get; set; }

    /// <summary>Most-recent-seen timestamp (UTC).</summary>
    public DateTime LastLogDate { get; set; }

    /// <summary>Soft-delete timestamp (UTC), or null when active.</summary>
    public DateTime? DeletionDate { get; set; }

    /// <summary>When true, the row is exempt from purge/soft-delete sweeps.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Captured request body, when the request-body middleware is enabled.</summary>
    public string? RequestBody { get; set; }
}
