using System.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Data.Probes.IntegrationTests;

public sealed class PostgresSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private NpgsqlConnection Open(string? searchPath = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        if (searchPath is not null)
        {
            builder.SearchPath = searchPath;
        }

        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private void Exec(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Probe_ShouldReportPublic_WhenTableLivesInPublic()
    {
        Exec("CREATE TABLE IF NOT EXISTS probe_public (id int)");

        using var connection = Open();
        var result = PostgresSchemaProbe.Probe(connection, "probe_public");

        Assert.Equal("public", result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldReportNullSchema_WhenTableDoesNotResolve()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS probe_missing_app");
        Exec("CREATE TABLE IF NOT EXISTS public.probe_missing (id int)");

        // search_path names only the app schema, so public.probe_missing is off the path.
        using var connection = Open(searchPath: "probe_missing_app");
        var result = PostgresSchemaProbe.Probe(connection, "probe_missing");

        Assert.Null(result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldReportBothCopies_WhenTableExistsInAppAndPublic()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS probe_both_app");
        Exec("CREATE TABLE IF NOT EXISTS probe_both_app.probe_both (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.probe_both (id int)");

        using var connection = Open(searchPath: "probe_both_app,public");
        var result = PostgresSchemaProbe.Probe(connection, "probe_both");

        Assert.Equal("probe_both_app", result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldResolveQuotedIdentifier_WhenTableNameIsCaseSensitive()
    {
        Exec("CREATE TABLE IF NOT EXISTS public.\"ProbeQuoted\" (id int)");

        using var connection = Open();
        var result = PostgresSchemaProbe.Probe(connection, "\"ProbeQuoted\"");

        Assert.Equal("public", result.ResolvedSchema);
    }

    [Fact]
    public void Probe_ShouldNotResolve_WhenQuotedTableIsProbedUnquoted()
    {
        Exec("CREATE TABLE IF NOT EXISTS public.\"ProbeUnquoted\" (id int)");

        using var connection = Open();
        // Unquoted folds to lower case: ProbeUnquoted != probeunquoted.
        var result = PostgresSchemaProbe.Probe(connection, "ProbeUnquoted");

        Assert.Null(result.ResolvedSchema);
    }
}
