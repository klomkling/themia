using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Probes;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Pdf.Migrations;
using Xunit;

namespace Themia.Modules.Pdf.SchemaProbe.IntegrationTests;

public sealed class PdfSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenPdfTemplatesIsOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS pdf_app";
            command.ExecuteNonQuery();
        }

        // Migrate once on the plain (default-search_path) connection string, not the pdf_app-scoped one
        // below, for the same reason ChallengesSchemaProbeTests does: PdfTemplateSchemaMigration's
        // filtered-unique-index statements are raw Execute.Sql, which follows this connection's actual
        // search_path rather than always landing in 'public' the way Create.Table does. Running the
        // migration itself against a search_path that excludes 'public' therefore fails before the probe
        // this test targets ever runs. Pre-applying the migration here keeps the assertion on the probe.
        ThemiaMigrations.Run(
            MigrationEngine.Postgres, builder.ConnectionString, typeof(PdfTemplateSchemaMigration).Assembly);

        builder.SearchPath = "pdf_app";

        var warnings = new List<string>();
        using var host = BuildHost(builder.ConnectionString, DatabaseProviderNames.Postgres, warnings);

        var ex = await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
        Assert.Contains("pdf_templates", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ShouldStart_WhenTheProviderIsNotPostgres()
    {
        // The probe must not run at all off PostgreSQL; an unreachable connection string proves it
        // was never opened. A connection failure only WARNS (it never throws), so "the host
        // started" alone would pass even with a broken `appliesTo` -- capture logger output and
        // assert nothing was logged, the way Themia.Data.Probes.IntegrationTests does.
        var warnings = new List<string>();
        using var host = BuildHost(
            "Server=127.0.0.1;Port=1;Database=nothing;Uid=nobody;Pwd=nobody;",
            DatabaseProviderNames.SqlServer,
            warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Host_ShouldStart_WhenNoDatabaseProviderIsRegistered()
    {
        // The real Dapper path (AddThemiaDapperCore / AddThemiaDapperPostgres/MySql/SqlServer) never
        // registers IDatabaseProvider -- only AddThemiaDbContext (EF Core) does. appliesTo must treat
        // an absent provider as "not PostgreSQL as far as we can tell" and skip, not throw. An
        // unreachable connection string proves the probe never actually ran: a probe that wrongly ran
        // would produce a warning (connection failure), not silence.
        var warnings = new List<string>();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] =
                        "Server=127.0.0.1;Port=1;Database=nothing;Uid=nobody;Pwd=nobody;",
                }))
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services => services.AddThemiaPdfModuleDapper())
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    private static IHost BuildHost(string connectionString, string providerName, List<string> warnings)
        => new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }))
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDatabaseProvider>(new StubDatabaseProvider(providerName));
                services.AddThemiaPdfModuleDapper();
            })
            .Build();

    private sealed class StubDatabaseProvider(string providerName) : IDatabaseProvider
    {
        public string ProviderName { get; } = providerName;

        public void Configure(
            Microsoft.EntityFrameworkCore.DbContextOptionsBuilder optionsBuilder,
            IConfiguration configuration,
            IServiceProvider serviceProvider)
        {
            // Not exercised: these tests only wire the Dapper peer, which never calls AddDbContext.
        }

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // Not exercised: these tests only wire the Dapper peer, which never calls this member.
        }
    }
}

/// <summary>Copied from <c>Themia.Data.Probes.IntegrationTests</c> rather than making that test
/// project's helper public API.</summary>
internal sealed class CapturingLoggerProvider(List<string> warnings) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(warnings);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
