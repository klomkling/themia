using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Audit;
using Themia.Audit.DependencyInjection;
using Themia.Audit.Migrations;
using Themia.Audit.PostgreSql;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Audit.DependencyInjection;
using Xunit;

namespace Themia.Modules.Audit.IntegrationTests;

/// <summary>The data layer a rollback/commit scenario runs on. Both are first-class peers per DECISION #6,
/// so the same transaction-policy behavior is asserted against both.</summary>
public enum DataLayer
{
    /// <summary>EF Core, via <see cref="TestAuditDbContext"/>.</summary>
    EfCore,

    /// <summary>Dapper, via <c>DapperUnitOfWork</c>.</summary>
    Dapper,
}

/// <summary>
/// Real transaction-semantics tests for <see cref="TransactionalAuditRecorder"/>: one PostgreSQL container
/// for the whole project (design brief item 6 — transaction semantics are not engine-specific, so one
/// engine covers both data layers). Every test namespaces its own rows with a per-test tenant id, so
/// distinct tests never collide on the same table even though they share one container (modelled on
/// <c>Themia.Audit.IntegrationTests.AuditStoreFixtures</c>'s isolation note).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditTransactionIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnectionString, typeof(AuditSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Theory]
    [InlineData(DataLayer.EfCore)]
    [InlineData(DataLayer.Dapper)]
    public async Task Activity_rows_roll_back_with_the_business_write(DataLayer layer)
    {
        var tenantId = Guid.NewGuid().ToString();
        await using var provider = BuildHost(layer, tenantId);
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        await Assert.ThrowsAnyAsync<Exception>(() => unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await recorder.RecordAsync(Activity(), null, ct);
            throw new InvalidOperationException("business failure");
        }));

        Assert.Equal(0, await CountAuditRowsAsync(tenantId));
    }

    [Theory]
    [InlineData(DataLayer.EfCore)]
    [InlineData(DataLayer.Dapper)]
    public async Task Activity_rows_commit_with_the_business_write(DataLayer layer)
    {
        var tenantId = Guid.NewGuid().ToString();
        await using var provider = BuildHost(layer, tenantId);
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        await unitOfWork.ExecuteInTransactionAsync(
            async ct => await recorder.RecordAsync(Activity(), null, ct));

        Assert.Equal(1, await CountAuditRowsAsync(tenantId));
    }

    [Fact]
    public async Task Authentication_rows_survive_a_real_surrounding_rollback()
    {
        var tenantId = Guid.NewGuid().ToString();
        await using var provider = BuildHost(DataLayer.EfCore, tenantId);
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        await Assert.ThrowsAnyAsync<Exception>(() => unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await recorder.RecordAsync(Authentication(), null, ct);
            throw new InvalidOperationException("business failure");
        }));

        // Category.Authentication is hardwired to Never: the row was written on its own connection, so
        // the surrounding business transaction's rollback never touched it.
        Assert.Equal(1, await CountAuditRowsAsync(tenantId));
    }

    private ServiceProvider BuildHost(DataLayer layer, string tenantId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId(tenantId)));

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = ConnectionString;
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        services.AddThemiaAuditPostgreSql();
        services.AddThemiaAuditModule();

        switch (layer)
        {
            case DataLayer.EfCore:
                services.AddThemiaPostgres<TestAuditDbContext>(configuration);
                services.AddThemiaDataRepositories<TestAuditDbContext>();
                break;
            case DataLayer.Dapper:
                services.AddThemiaDapperPostgres(configuration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown DataLayer value.");
        }

        return services.BuildServiceProvider();
    }

    private async Task<long> CountAuditRowsAsync(string tenantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM themia_audit_events WHERE tenant_id = @TenantId", connection);
        command.Parameters.AddWithValue("TenantId", tenantId);
        return (long)(await command.ExecuteScalarAsync())!;
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
