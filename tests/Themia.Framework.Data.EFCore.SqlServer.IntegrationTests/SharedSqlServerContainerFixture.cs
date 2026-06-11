using Testcontainers.MsSql;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests;

/// <summary>
/// One SQL Server container shared by every suite in this assembly (the assembly is serialized, so a
/// single instance suffices). Each suite isolates itself in its own database on this server via
/// <see cref="GetConnectionString"/>; EnsureCreated creates the database + schema on first use.
/// </summary>
public sealed class SharedSqlServerContainerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly MsSqlContainer container = new MsSqlBuilder(Image).WithCleanUp(true).Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Connection string targeting the given suite-private database on the shared server.</summary>
    public string GetConnectionString(string databaseName)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerIntegrationCollection : ICollectionFixture<SharedSqlServerContainerFixture>
{
    public const string Name = "SqlServerIntegration";
}
