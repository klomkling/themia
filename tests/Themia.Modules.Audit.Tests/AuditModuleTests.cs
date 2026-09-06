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
}
