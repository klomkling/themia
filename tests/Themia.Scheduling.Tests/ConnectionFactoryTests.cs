using System;
using System.Data.Common;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Themia.Scheduling.DependencyInjection;
using Xunit;

namespace Themia.Scheduling.Tests;

/// <summary>
/// The execution-history store's connection factory (coord #0126). It used to <c>switch</c> on
/// <see cref="MigrationEngine"/> and construct <c>NpgsqlConnection</c> / <c>SqlConnection</c> itself,
/// which forced this package to declare BOTH drivers — so a PostgreSQL-only adopter still took
/// <c>Microsoft.Data.SqlClient</c> and the JWT stack behind it. It now routes through
/// <see cref="IMigrationEngineAdapter.CreateConnection"/>.
/// </summary>
/// <remarks>
/// The integration suite constructs its own connections to exercise
/// <c>DapperExecutionHistoryStore</c>, so it never reaches this factory. These tests are the only
/// coverage of the path DI actually builds.
/// </remarks>
public class ConnectionFactoryTests
{
    private const string ConnectionString = "Host=localhost;Database=unused";

    [Fact]
    public void ConnectionFactory_ResolvesTheAdapterLazily_SoRegistrationOrderDoesNotMatter()
    {
        // Built BEFORE any adapter is registered. Resolving eagerly here would make
        // AddThemiaDataMigrations{Engine}() have to precede AddThemiaScheduling, which is exactly the
        // "position-independent in startup" property MIGRATION.md promises for this package.
        var factory = SchedulingServiceCollectionExtensions.ConnectionFactory(
            MigrationEngine.Postgres, ConnectionString);

        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

        using DbConnection connection = factory();
        Assert.Equal("NpgsqlConnection", connection.GetType().Name);
    }

    [Fact]
    public void ConnectionFactory_ReturnsAPooledConnection()
    {
        // CreateConnection, NOT CreateUnpooledConnection. The unpooled one exists for the migration
        // lock, which is held for a whole migration; a job-store connection is short-lived and must go
        // back to the pool. Swapping the two would be invisible except here — the unpooled factory
        // stamps Pooling=False into the connection string.
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

        var factory = SchedulingServiceCollectionExtensions.ConnectionFactory(
            MigrationEngine.Postgres, ConnectionString);

        using DbConnection connection = factory();
        Assert.DoesNotContain("Pooling=False", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MigrationEngine.MySql)]
    [InlineData((MigrationEngine)999)]
    public void ConnectionFactory_RejectsAnUnsupportedEngineEagerly(MigrationEngine engine)
    {
        // Themia.Scheduling supports PostgreSQL and SQL Server only, and says so at REGISTRATION —
        // not later, from inside a lazily-invoked delegate, where an operator would meet it as a
        // failure of the first job instead of a failure to start.
        Assert.Throws<NotSupportedException>(
            () => SchedulingServiceCollectionExtensions.ConnectionFactory(engine, ConnectionString));
    }
}
