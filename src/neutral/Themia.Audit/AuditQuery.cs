namespace Themia.Audit;

/// <summary>
/// Filters and paging for a read against <c>themia_audit_events</c>. Every filter is optional; an
/// empty query returns every row visible to the caller.
/// </summary>
public sealed record AuditQuery
{
    /// <summary>
    /// The tenant to filter by. <see langword="null"/> means <em>no filter</em> — both tenant-owned and
    /// host-level rows are returned. This is distinct from <see cref="HostLevelOnly"/>, which a single
    /// nullable field cannot express.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>When <see langword="true"/>, returns only rows where <c>tenant_id IS NULL</c>.</summary>
    public bool HostLevelOnly { get; init; }

    /// <summary>Filters to events performed by this actor.</summary>
    public string? ActorId { get; init; }

    /// <summary>Filters to events concerning entities of this type.</summary>
    public string? EntityType { get; init; }

    /// <summary>Filters to events concerning this entity.</summary>
    public string? EntityId { get; init; }

    /// <summary>Filters to this category.</summary>
    public AuditCategory? Category { get; init; }

    /// <summary>Filters to this outcome.</summary>
    public AuditOutcome? Outcome { get; init; }

    /// <summary>Filters to events that occurred at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Filters to events that occurred at or before this instant.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>The 1-based page to return. Defaults to <c>1</c>.</summary>
    public int Page { get; init; } = 1;

    /// <summary>The number of rows per page. Defaults to <c>50</c>.</summary>
    public int PageSize { get; init; } = 50;
}
