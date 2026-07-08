using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Modules.Pdf.Migrations;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests.Fixtures;

public sealed class PdfSqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.SqlServer;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(PdfTemplateSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE pdf_templates;";
        await command.ExecuteNonQueryAsync();
    }
}
