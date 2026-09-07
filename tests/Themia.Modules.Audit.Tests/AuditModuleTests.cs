using Microsoft.Extensions.DependencyInjection;
using Themia.Audit;
using Themia.Audit.DependencyInjection;
using Xunit;

namespace Themia.Modules.Audit.Tests;

public class AuditModuleTests
{
    [Fact]
    public async Task InitializeAsync_succeeds_when_the_schema_already_exists()
    {
        var services = BuildServices(schemaExists: true);
        await using var provider = services.BuildServiceProvider();

        var module = new AuditModule();

        await module.InitializeAsync(provider); // Does not throw.
    }

    [Fact]
    public async Task InitializeAsync_throws_naming_the_table_when_the_schema_is_missing()
    {
        var services = BuildServices(schemaExists: false);
        await using var provider = services.BuildServiceProvider();

        var module = new AuditModule();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => module.InitializeAsync(provider).AsTask());
        Assert.Contains("themia_audit_events", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_surfaces_the_real_cause_for_a_non_missing_table_failure()
    {
        // A permission error, a timeout, or bad credentials also throws a DbException from QueryAsync —
        // but is not "the table is missing". The wrapped message must carry the real cause so an operator
        // does not go hunting for a table that is already there, and the original exception must survive
        // as InnerException.
        const string underlyingMessage = "permission denied for relation themia_audit_events";
        var store = new FakeAuditStore { FailWith = new FakeDbException(underlyingMessage) };

        var services = new ServiceCollection();
        services.AddSingleton<IAuditStore>(store);
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        await using var provider = services.BuildServiceProvider();

        var module = new AuditModule();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => module.InitializeAsync(provider).AsTask());
        Assert.Contains(underlyingMessage, ex.Message, StringComparison.Ordinal);
        Assert.IsType<FakeDbException>(ex.InnerException);
        Assert.Equal(underlyingMessage, ex.InnerException!.Message);
    }

    [Fact]
    public void Descriptor_names_the_module()
    {
        Assert.Equal("Themia.Audit", new AuditModule().Descriptor.Name);
    }

    private static IServiceCollection BuildServices(bool schemaExists)
    {
        var store = new FakeAuditStore();
        if (!schemaExists)
        {
            store.FailWith = new FakeDbException("relation \"themia_audit_events\" does not exist");
        }

        var services = new ServiceCollection();
        services.AddSingleton<IAuditStore>(store);
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        return services;
    }
    // Pins the probe to a lookup that reads no rows. QueryAsync issues a second statement — the dialect's
    // CountSql, which has no predicate — so probing through it meant a full scan of an append-only,
    // keep-forever table on every host start and every pod restart.
    [Fact]
    public async Task InitializeAsync_probes_with_a_row_free_lookup_not_a_count()
    {
        var store = new FakeAuditStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditStore>(store);
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        await using var provider = services.BuildServiceProvider();

        await new AuditModule().InitializeAsync(provider);

        Assert.Equal(Guid.Empty, store.LastGetEventUid);
        Assert.Null(store.LastQuery);
    }
}
