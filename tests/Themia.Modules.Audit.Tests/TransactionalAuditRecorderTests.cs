using Themia.Audit;
using Themia.Audit.Redaction;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Modules.Audit.Tests;

public class TransactionalAuditRecorderTests
{
    private static readonly AuditOptions NeutralOptions = new() { ConnectionString = "fake", Engine = AuditEngine.Postgres };

    [Fact]
    public async Task RequireTransaction_throws_and_the_message_names_the_fix()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor(); // Ambient left null: no transaction open.
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.RequireTransaction);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.RecordAsync(Activity(), null, default).AsTask());

        Assert.Contains("ExecuteInTransactionAsync", ex.Message, StringComparison.Ordinal);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task RequireTransaction_with_an_ambient_transaction_writes_on_it()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Activity(), null, default);

        Assert.Same(ambient.Ambient!.Value.Connection, store.LastWriteConnection);
        Assert.Same(ambient.Ambient!.Value.Transaction, store.LastWriteTransaction);
    }

    [Fact]
    public async Task JoinIfPresent_writes_durably_with_no_transaction_and_does_not_throw()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor(); // No transaction open.
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.JoinIfPresent);

        await recorder.RecordAsync(Activity(), null, default);

        Assert.NotNull(store.LastWritten);
        Assert.Null(store.LastWriteTransaction); // Wrote on its own connection, unenlisted.
    }

    [Fact]
    public async Task JoinIfPresent_with_an_ambient_transaction_enlists()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.JoinIfPresent);

        await recorder.RecordAsync(Activity(), null, default);

        Assert.Same(ambient.Ambient!.Value.Connection, store.LastWriteConnection);
        Assert.Same(ambient.Ambient!.Value.Transaction, store.LastWriteTransaction);
    }

    [Fact]
    public async Task Authentication_rows_survive_a_surrounding_rollback()
    {
        // Category.Authentication is hardwired to Never regardless of AuditModuleOptions.ActivityPolicy:
        // even with a business transaction open (ambient set), the write must bypass it entirely so a
        // failed login is recorded durably even when the surrounding request rolls back.
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Authentication(), null, default);

        Assert.NotNull(store.LastWritten);
        Assert.NotSame(ambient.Ambient!.Value.Connection, store.LastWriteConnection);
        Assert.Null(store.LastWriteTransaction);
    }

    [Fact]
    public async Task UserLifecycle_rows_are_also_hardwired_to_Never()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Activity() with { Category = AuditCategory.UserLifecycle, EventType = "USER_DEACTIVATED" }, null, default);

        Assert.NotNull(store.LastWritten);
        Assert.Null(store.LastWriteTransaction);
    }

    [Fact]
    public async Task Tenant_resolved_from_context_when_entry_leaves_it_null()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: "tenant-a", AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Activity(), null, default);

        Assert.Equal("tenant-a", store.LastWritten!.TenantId);
    }

    [Fact]
    public async Task Tenant_is_never_invented_when_no_ambient_tenant_is_present()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: null, AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Activity(), null, default);

        Assert.Null(store.LastWritten!.TenantId);
    }

    [Fact]
    public async Task An_explicit_tenant_on_the_entry_is_not_overridden()
    {
        var store = new FakeAuditStore();
        var ambient = new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() };
        var recorder = CreateRecorder(store, ambient, tenantId: "ambient-tenant", AuditTransactionPolicy.RequireTransaction);

        await recorder.RecordAsync(Activity() with { TenantId = "explicit-tenant" }, null, default);

        Assert.Equal("explicit-tenant", store.LastWritten!.TenantId);
    }

    private static TransactionalAuditRecorder CreateRecorder(
        FakeAuditStore store, FakeAmbientConnectionAccessor ambient, string? tenantId, AuditTransactionPolicy activityPolicy)
    {
        var inner = new AuditRecorder(store, new AuditRedactor(new AuditRedactionOptions()), TimeProvider.System, new FakeAuditDialect(), NeutralOptions);
        var tenantContext = new FakeTenantContext(tenantId is null ? null : new TenantId(tenantId));
        var options = new AuditModuleOptions { ActivityPolicy = activityPolicy };
        return new TransactionalAuditRecorder(inner, ambient, tenantContext, options);
    }

    private static AuditEntry Activity() => new()
    {
        Category = AuditCategory.Activity,
        EventType = "TEST_EVENT",
        Outcome = AuditOutcome.Success,
    };

    private static AuditEntry Authentication() => new()
    {
        Category = AuditCategory.Authentication,
        EventType = "LOGIN_FAILED",
        Outcome = AuditOutcome.Failure,
    };
}
