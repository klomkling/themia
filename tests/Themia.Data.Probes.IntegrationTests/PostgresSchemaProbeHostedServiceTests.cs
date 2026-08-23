using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Data.Probes.IntegrationTests;

public sealed class PostgresSchemaProbeHostedServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private string ConnectionString(string? searchPath)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        if (searchPath is not null)
        {
            builder.SearchPath = searchPath;
        }

        return builder.ConnectionString;
    }

    private void Exec(string sql)
    {
        using var connection = new NpgsqlConnection(container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IHost BuildHost(
        string connectionString,
        string[] tables,
        List<string> warnings,
        Func<IServiceProvider, bool>? appliesTo = null)
        => new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services => services.AddPostgresSchemaProbe(
                "Themia.Test",
                _ =>
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                },
                tables,
                appliesTo))
            .Build();

    [Fact]
    public async Task Host_ShouldStart_WhenTableResolvesOutsidePublic()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_app_only");
        Exec("CREATE TABLE IF NOT EXISTS hs_app_only.hs_only (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_app_only"), ["hs_only"], warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTableDoesNotResolve()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_missing_app");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_missing (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_missing_app"), ["hs_missing"], warnings);

        var ex = await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
        Assert.Contains("hs_missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ShouldWarn_WhenAStrayPublicCopyExists()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_both_app");
        Exec("CREATE TABLE IF NOT EXISTS hs_both_app.hs_both (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_both (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_both_app,public"), ["hs_both"], warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(warnings);
        Assert.Contains("hs_both_app", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ShouldStartAndWarn_WhenTheDatabaseIsUnreachable()
    {
        // A connection failure is an availability fault, not a configuration fault. Throwing here
        // would newly make host startup depend on database uptime.
        var warnings = new List<string>();
        using var host = BuildHost(
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
            ["anything"],
            warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(warnings);
    }

    [Fact]
    public async Task Host_ShouldSkipTheProbe_WhenAppliesToIsFalse()
    {
        var warnings = new List<string>();
        using var host = BuildHost(
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
            ["anything"],
            warnings,
            appliesTo: _ => false);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Host_ShouldRunEveryRegistration_WhenTwoProbesAreRegistered()
    {
        // AddHostedService<T> de-duplicates by implementation type, which would silently collapse
        // the second registration. The extension must not use it.
        Exec("CREATE SCHEMA IF NOT EXISTS hs_two_app");
        Exec("CREATE TABLE IF NOT EXISTS hs_two_app.hs_two_a (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_two_a (id int)");
        Exec("CREATE TABLE IF NOT EXISTS hs_two_app.hs_two_b (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_two_b (id int)");

        var warnings = new List<string>();
        var connectionString = ConnectionString("hs_two_app,public");

        using var host = new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services =>
            {
                IDbConnection Factory(IServiceProvider _)
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                }

                services.AddPostgresSchemaProbe("Themia.A", Factory, ["hs_two_a"]);
                services.AddPostgresSchemaProbe("Themia.B", Factory, ["hs_two_b"]);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(2, warnings.Count);
    }
}

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
