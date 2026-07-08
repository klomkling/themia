using MySqlConnector;
using Testcontainers.MySql;
using Themia.Data.Migrations;
using Themia.Modules.Pdf.Migrations;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests.Fixtures;

public sealed class PdfMySqlFixture : IAsyncLifetime
{
    // MySQL 8.0.13+ is required for the functional key parts the pdf_templates unique indexes use.
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4")
        .WithDatabase("themia_pdf_tests")
        .WithUsername("themia")
        .WithPassword("themia")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.MySql;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(PdfTemplateSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE pdf_templates;";
        await command.ExecuteNonQueryAsync();
    }
}
