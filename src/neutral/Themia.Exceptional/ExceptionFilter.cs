namespace Themia.Exceptional;

/// <summary>Filter + paging for querying stored exceptions (dashboard support).</summary>
public sealed class ExceptionFilter
{
    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (rows per page).</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Inclusive lower bound on <see cref="ExceptionEntry.CreationDate"/> (UTC).</summary>
    public DateTime? From { get; set; }

    /// <summary>Inclusive upper bound on <see cref="ExceptionEntry.CreationDate"/> (UTC).</summary>
    public DateTime? To { get; set; }

    /// <summary>Filter by application name.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>Filter by tenant id.</summary>
    public string? TenantId { get; set; }

    /// <summary>Free-text match against Type/Message.</summary>
    public string? Search { get; set; }

    /// <summary>Include soft-deleted rows when true.</summary>
    public bool IncludeDeleted { get; set; }
}
