using System.Data.Common;

using Testcontainers.MySql;

using Themia.Data.Migrations;
using Themia.Exceptional.Migrations;

using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

/// <summary>
/// One MySQL container shared by every test class in this assembly via xUnit's collection-fixture
/// mechanism (<see cref="MySqlExceptionalCollection"/>), instead of each test class/method starting its
/// own. The schema migration runs once, here, in <see cref="InitializeAsync"/>.
/// </summary>
public sealed class MySqlExceptionalFixture : IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has
    /// completed.</summary>
    public string ConnectionString => container.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.MySql, ConnectionString, typeof(ExceptionLogMigration).Assembly);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Deletes every row from <c>Exceptions</c> so the next test starts from an empty table —
    /// replaces the isolation a fresh-per-test container used to provide.</summary>
    public async Task ResetAsync()
    {
        await using DbConnection connection = new MySqlExceptionalDialect(ConnectionString).CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM `Exceptions`";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>xUnit collection tying every test class in this assembly to one shared
/// <see cref="MySqlExceptionalFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlExceptionalCollection : ICollectionFixture<MySqlExceptionalFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Exceptional";
}
