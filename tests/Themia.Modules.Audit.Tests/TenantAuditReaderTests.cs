using Themia.Audit;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Modules.Audit.Tests;

public class TenantAuditReaderTests
{
    private static readonly AuditOptions NeutralOptions = new() { ConnectionString = "fake", Engine = AuditEngine.Postgres };

    // 2 tenant-A rows, 3 tenant-B rows, 1 host-level row: 6 total.
    private static FakeAuditStore SeededStore() => new(
        Entry("tenant-a", "A1"),
        Entry("tenant-a", "A2"),
        Entry("tenant-b", "B1"),
        Entry("tenant-b", "B2"),
        Entry("tenant-b", "B3"),
        Entry(null, "HOST"));

    [Fact]
    public async Task TenantAuditReader_returns_only_the_ambient_tenants_rows()
    {
        var store = SeededStore();
        var reader = CreateReader(store, "tenant-a");

        var page = await reader.QueryAsync(new AuditQuery(), default);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, e => Assert.Equal("tenant-a", e.TenantId));
    }

    [Fact]
    public async Task TenantAuditReader_ignores_a_query_naming_another_tenant()
    {
        var store = SeededStore();
        var reader = CreateReader(store, "tenant-a");

        var page = await reader.QueryAsync(new AuditQuery { TenantId = "tenant-b" }, default);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, e => Assert.Equal("tenant-a", e.TenantId));
    }

    [Fact]
    public async Task Raw_store_query_crosses_tenants_by_design()
    {
        // Documents the fail-open neutral surface (design §9) rather than leaving it to be discovered:
        // IAuditStore.QueryAsync applies no tenant predicate unless the caller's AuditQuery names one.
        var store = SeededStore();
        using var connection = new StubDbConnection();

        var page = await store.QueryAsync(new AuditQuery(), connection, default);

        Assert.Equal(6, page.Total);
    }

    private static TenantAuditReader CreateReader(FakeAuditStore store, string? tenantId) =>
        new(store, new FakeAuditDialect(), NeutralOptions, new FakeTenantContext(tenantId is null ? null : new TenantId(tenantId)));

    private static AuditEntry Entry(string? tenantId, string eventType) => new()
    {
        TenantId = tenantId,
        Category = AuditCategory.Activity,
        EventType = eventType,
        Outcome = AuditOutcome.Success,
    };
}
