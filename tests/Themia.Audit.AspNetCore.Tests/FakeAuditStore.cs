using System.Data.Common;

namespace Themia.Audit.AspNetCore.Tests;

/// <summary>In-memory <see cref="IAuditStore"/> for endpoint tests. Filters/pages/sorts the same way the
/// real dialect-backed store would (design §6), so tests can assert on rendered output; records the last
/// query passed to <see cref="QueryAsync"/> for direct assertions on the parsed request.</summary>
internal sealed class FakeAuditStore : IAuditStore
{
    private readonly List<AuditEntry> entries;

    public FakeAuditStore(params AuditEntry[] entries) => this.entries = entries.ToList();

    /// <summary>The query built from the last request's query string, for assertions on parsing/clamping.</summary>
    public AuditQuery? LastQuery { get; private set; }

    /// <summary>When set, every method throws it (simulates a store/connection failure).</summary>
    public Exception? FailWith { get; init; }

    public Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw FailWith;
        entries.Add(entry);
        return Task.FromResult((long)entries.Count);
    }

    public Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (FailWith is not null) throw FailWith;

        IEnumerable<AuditEntry> filtered = entries;
        if (query.HostLevelOnly) filtered = filtered.Where(e => e.TenantId is null);
        else if (query.TenantId is not null) filtered = filtered.Where(e => e.TenantId == query.TenantId);
        if (query.ActorId is not null) filtered = filtered.Where(e => e.ActorId == query.ActorId);
        if (query.EntityType is not null) filtered = filtered.Where(e => e.EntityType == query.EntityType);
        if (query.EntityId is not null) filtered = filtered.Where(e => e.EntityId == query.EntityId);
        if (query.Category is not null) filtered = filtered.Where(e => e.Category == query.Category);
        if (query.Outcome is not null) filtered = filtered.Where(e => e.Outcome == query.Outcome);
        if (query.From is not null) filtered = filtered.Where(e => e.OccurredAt >= query.From);
        if (query.To is not null) filtered = filtered.Where(e => e.OccurredAt <= query.To);

        var ordered = filtered.OrderByDescending(e => e.OccurredAt).ToList();
        var page = ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        return Task.FromResult(new PagedResult<AuditEntry> { Items = page, Total = ordered.Count });
    }

    public Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw FailWith;
        return Task.FromResult(entries.FirstOrDefault(e => e.EventUid == eventUid));
    }

    public Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}
