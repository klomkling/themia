using System.Data.Common;

using Testcontainers.MsSql;

using Themia.Data.Migrations;
using Themia.Exceptional.Migrations;

using Xunit;

namespace Themia.Exceptional.SqlServer.IntegrationTests;

/// <summary>
/// One SQL Server container shared by every test class in this assembly via xUnit's collection-fixture
/// mechanism (<see cref="SqlServerExceptionalCollection"/>), instead of each test class/method starting
/// its own. The schema migration runs once, here, in <see cref="InitializeAsync"/>.
/// </summary>
public sealed class SqlServerExceptionalFixture : IAsyncLifetime
{
    // MsSqlBuilder 4.12.0: parameterless ctor is [Obsolete] as error — must pass image explicitly.
    // Pinned (not :2022-latest) for reproducible CI; this is Testcontainers.MsSql 4.12.0's default image.
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has
    /// completed.</summary>
    public string ConnectionString => container.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, ConnectionString, typeof(ExceptionLogMigration).Assembly);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Deletes every row from <c>Exceptions</c> so the next test starts from an empty table —
    /// replaces the isolation a fresh-per-test container used to provide.</summary>
    public async Task ResetAsync()
    {
        await using DbConnection connection = new SqlServerExceptionalDialect(ConnectionString).CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM [Exceptions]";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>xUnit collection tying every test class in this assembly to one shared
/// <see cref="SqlServerExceptionalFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerExceptionalCollection : ICollectionFixture<SqlServerExceptionalFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Exceptional";
}
