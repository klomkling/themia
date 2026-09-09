using System.Data.Common;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Exceptional.Migrations;

using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

/// <summary>
/// One PostgreSQL container shared by every test class in this assembly via xUnit's collection-fixture
/// mechanism (<see cref="PostgresExceptionalCollection"/>) — both <see cref="PostgresExceptionStoreTests"/>
/// and <see cref="ExceptionalSchemaProbeTests"/> join it — instead of each test class/method starting its
/// own. The schema migration runs once, here, in <see cref="InitializeAsync"/>; it always lands in the
/// <c>public</c> schema (FluentMigrator's <c>Create.Table</c> ignores <c>search_path</c>), so it cannot
/// collide with the extra schema <see cref="ExceptionalSchemaProbeTests"/> creates for its own assertion.
/// </summary>
public sealed class PostgresExceptionalFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has
    /// completed.</summary>
    public string ConnectionString => container.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnectionString, typeof(ExceptionLogMigration).Assembly);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Deletes every row from <c>Exceptions</c> so the next test starts from an empty table —
    /// replaces the isolation a fresh-per-test container used to provide.</summary>
    public async Task ResetAsync()
    {
        await using DbConnection connection = new PostgresExceptionalDialect(ConnectionString).CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """DELETE FROM "Exceptions";""";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>xUnit collection tying every test class in this assembly to one shared
/// <see cref="PostgresExceptionalFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresExceptionalCollection : ICollectionFixture<PostgresExceptionalFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "PostgreSql Exceptional";
}
