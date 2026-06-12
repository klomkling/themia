using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Scheduling;
using Themia.Quartz;
using Xunit;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Lifecycle integration tests for <see cref="SchedulingModule"/> against a real database:
/// <see cref="SchedulingModule.InitializeAsync"/> applies the FluentMigrator schema migration, and the
/// registered <see cref="IExecutionHistoryStore"/> resolves to the EF-backed store and round-trips data.
/// Run once per engine via the derived classes.
/// </summary>
public abstract class SchedulingModuleTestsBase
{
    protected abstract string ConnectionString { get; }
    protected abstract string ProviderName { get; }

    [Fact]
    public async Task InitializeAsync_RunsFmMigration_AndStoreRoundTrips()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        // The table exists (FM migration applied) — CountAsync would throw if it were absent.
        var existingCount = await context.ExecutionHistory.CountAsync();
        Assert.Equal(0, existingCount);

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

    [Fact]
    public async Task DefaultAuthorize_AllowsAuthenticated_DeniesAnonymous()
    {
        var provider = BuildModuleServices();
        var authorize = provider.GetRequiredService<ThemiaQuartzOptions>().Authorize;
        Assert.NotNull(authorize);

        var authenticatedCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
        };
        Assert.True(await authorize!(authenticatedCtx));

        var anonymousCtx = new DefaultHttpContext();
        Assert.False(await authorize(anonymousCtx));
    }

    private ServiceProvider BuildModuleServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(ProviderName));

        new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" })
            .ConfigureServices(services);

        return services.BuildServiceProvider();
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_scheduling_module_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.Postgres;

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class SqlServerSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.SqlServer;

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
