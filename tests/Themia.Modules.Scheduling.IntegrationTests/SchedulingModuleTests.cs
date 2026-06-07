using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Themia.Modules.Scheduling;
using Themia.Quartz;
using Xunit;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Lifecycle integration tests for <see cref="SchedulingModule"/> against a real PostgreSQL instance:
/// running the module's EF migration on <see cref="SchedulingModule.InitializeAsync"/> creates the
/// scheduling schema, and the registered <see cref="IExecutionHistoryStore"/> resolves to the
/// EF-backed store and round-trips data.
/// </summary>
[Trait("Category", "Integration")]
public class SchedulingModuleTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_scheduling_module_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task InitializeAsync_RunsMigration_AndStoreRoundTrips()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        // Act: run module startup → applies the InitialScheduling migration.
        await module.InitializeAsync(provider);

        // Assert (a): the scheduling schema/tables exist — the migration ran.
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);

        // A query against the migrated table succeeds (would throw if the table were absent).
        var existingCount = await context.ExecutionHistory.CountAsync();
        Assert.Equal(0, existingCount);

        // Assert (b): IExecutionHistoryStore resolves to the EF-backed store and round-trips.
        var store = scope.ServiceProvider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var entry = new ExecutionHistoryEntry
        {
            FireInstanceId = "module-fire-1",
            SchedulerName = "module-test",
            SchedulerInstanceId = "instance-1",
            Job = "job-a",
            Trigger = "trigger-a",
            ActualFireTimeUtc = DateTimeOffset.UtcNow,
            ScheduledFireTimeUtc = DateTimeOffset.UtcNow,
        };

        await store.Save(entry);
        var retrieved = await store.Get("module-fire-1");

        Assert.NotNull(retrieved);
        Assert.Equal("job-a", retrieved!.Job);
    }

    [Fact]
    public void ConfigureServices_RegistersStoreAndDashboardOptions()
    {
        var provider = BuildModuleServices();

        var store = provider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var quartzOptions = provider.GetRequiredService<ThemiaQuartzOptions>();
        Assert.Equal("/jobs", quartzOptions.VirtualPathRoot);
        Assert.NotNull(quartzOptions.Authorize);
    }

    private ServiceProvider BuildModuleServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = container.GetConnectionString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });
        module.ConfigureServices(services);

        return services.BuildServiceProvider();
    }
}
