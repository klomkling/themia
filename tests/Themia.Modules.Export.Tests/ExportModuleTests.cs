using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.DependencyInjection;
using Themia.Modules.Export.Jobs;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportModuleTests
{
    [Fact]
    public async Task InitializeAsync_throws_when_no_scheduler_registered()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        var module = new ExportModule(MigrationEngine.Postgres);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await module.InitializeAsync(provider));
        Assert.Contains("ISchedulerFactory", ex.Message, StringComparison.Ordinal);

        // Sanity: ISchedulerFactory really is absent from the bare collection.
        Assert.Null(provider.GetService<ISchedulerFactory>());
    }

    [Fact]
    public async Task Resolved_tenant_context_reports_BackgroundTenantScope_tenant()
    {
        var services = new ServiceCollection();
        services.AddThemiaExportModule();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

            Assert.True(tenantContext.HasTenant);
            Assert.Equal("acme", tenantContext.CurrentTenantId?.Value);
        }
    }
}
