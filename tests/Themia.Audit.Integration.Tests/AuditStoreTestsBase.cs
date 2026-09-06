using Xunit;

namespace Themia.Audit.Integration.Tests;

/// <summary>
/// Engine-agnostic store round-trip assertions, run once per engine by a thin subclass bound to that
/// engine's dedicated container (see <see cref="AuditStoreFixture"/>).
/// </summary>
public abstract class AuditStoreTestsBase(IAuditStore store, IAuditDialect dialect, string connectionString)
{
    [Fact]
    public async Task Every_field_lands_in_its_own_column()
    {
        // Distinct values per field: five consecutive nullable strings make a transposition invisible,
        // which is the defect that shipped in 0.21.4's outbox dialects.
        var entry = new AuditEntry
        {
            EventType = "EVT",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Failure,
            TenantId = "tenant-A",
            ActorId = "actor-1",
            ActorName = "actor-name-2",
            EntityType = "entity-type-3",
            EntityId = "entity-id-4",
            Reason = "reason-5",
            CorrelationId = "corr-6",
            IpAddress = "10.0.0.1",
            UserAgent = "agent-7",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        await using var conn = await OpenConnectionAsync();
        await store.WriteAsync(entry, conn, null, default);
        var read = await store.GetAsync(entry.EventUid, conn, default);

        Assert.NotNull(read);
        Assert.Equal("tenant-A", read.TenantId);
        Assert.Equal(AuditCategory.Activity, read.Category);
        Assert.Equal("EVT", read.EventType);
        Assert.Equal(AuditOutcome.Failure, read.Outcome);
        Assert.Equal("actor-1", read.ActorId);
        Assert.Equal("actor-name-2", read.ActorName);
        Assert.Equal("entity-type-3", read.EntityType);
        Assert.Equal("entity-id-4", read.EntityId);
        Assert.Equal("reason-5", read.Reason);
        Assert.Equal("corr-6", read.CorrelationId);
        Assert.Equal("10.0.0.1", read.IpAddress);
        Assert.Equal("agent-7", read.UserAgent);
    }

    [Fact]
    public async Task Null_tenant_round_trips_as_null_not_empty_string()
    {
        var entry = Valid() with { TenantId = null };

        await using var conn = await OpenConnectionAsync();
        await store.WriteAsync(entry, conn, null, default);
        var read = await store.GetAsync(entry.EventUid, conn, default);

        Assert.Null(read!.TenantId);
    }

    [Fact]
    public async Task Write_returns_increasing_ids()
    {
        await using var conn = await OpenConnectionAsync();
        var first = await store.WriteAsync(Valid(), conn, null, default);
        var second = await store.WriteAsync(Valid(), conn, null, default);

        Assert.True(second > first);
    }

    [Fact]
    public async Task Paging_is_stable_when_rows_share_a_timestamp()
    {
        // Scoped to a per-test tenant id so this class's rows never mix with another test method's
        // rows in the same shared container/table.
        var tenantId = $"paging-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow;

        await using var conn = await OpenConnectionAsync();
        for (var i = 0; i < 10; i++)
        {
            await store.WriteAsync(Valid() with { TenantId = tenantId, OccurredAt = at }, conn, null, default);
        }

        var query = new AuditQuery { TenantId = tenantId, Page = 1, PageSize = 5 };
        var p1 = await store.QueryAsync(query, conn, default);
        var p2 = await store.QueryAsync(query with { Page = 2 }, conn, default);

        Assert.Equal(5, p1.Items.Count);
        Assert.Equal(5, p2.Items.Count);
        Assert.Equal(10, p1.Total);
        Assert.Empty(p1.Items.Select(x => x.EventUid).Intersect(p2.Items.Select(x => x.EventUid)));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_event_uid()
    {
        await using var conn = await OpenConnectionAsync();
        var read = await store.GetAsync(Guid.NewGuid(), conn, default);

        Assert.Null(read);
    }

    [Fact]
    public async Task PurgeAsync_deletes_only_rows_older_than_the_cutoff()
    {
        var tenantId = $"purge-{Guid.NewGuid():N}";
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = DateTimeOffset.UtcNow;

        await using var conn = await OpenConnectionAsync();
        var oldEntry = Valid() with { TenantId = tenantId, OccurredAt = old };
        var recentEntry = Valid() with { TenantId = tenantId, OccurredAt = recent };
        await store.WriteAsync(oldEntry, conn, null, default);
        await store.WriteAsync(recentEntry, conn, null, default);

        await store.PurgeAsync(DateTimeOffset.UtcNow.AddDays(-1), conn, default);

        Assert.Null(await store.GetAsync(oldEntry.EventUid, conn, default));
        Assert.NotNull(await store.GetAsync(recentEntry.EventUid, conn, default));
    }

    private async Task<System.Data.Common.DbConnection> OpenConnectionAsync()
    {
        var conn = dialect.CreateConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static AuditEntry Valid() => new()
    {
        EventType = "EVT",
        Category = AuditCategory.Activity,
        Outcome = AuditOutcome.Success,
        OccurredAt = DateTimeOffset.UtcNow,
    };
}
