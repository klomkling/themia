using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public class AddThemiaExceptionalProviderTests
{
    [Fact]
    public void Registers_Store_Options_Sink_And_Enricher()
    {
        var services = new ServiceCollection();
        services.AddThemiaExceptionalProvider(
            dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
            configure: o => o.ApplicationName = "App",
            engine: MigrationEngine.Postgres, // unused when runMigration is false
            runMigration: false);

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IExceptionStore>());
        Assert.NotNull(sp.GetService<ExceptionalOptions>());
        Assert.NotNull(sp.GetService<ExceptionalSerilogSink>());
        Assert.NotNull(sp.GetService<HttpContextEnricher>());
    }

    [Fact]
    public void RunMigration_WithNullConnectionString_Throws()
    {
        var services = new ServiceCollection();

        // null → ArgumentNullException, whitespace → ArgumentException; both derive from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => services.AddThemiaExceptionalProvider(
            dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
            configure: o => o.ApplicationName = "App",
            engine: MigrationEngine.Postgres,
            connectionString: null,
            runMigration: true));
    }

    [Fact]
    public void RunMigration_Failure_WrapsInInvalidOperationException_NamingTheEngine()
    {
        var services = new ServiceCollection();

        // A connection string the Postgres processor cannot use fails inside the shared runner,
        // exercising the wrap-and-name behavior propagated from ThemiaMigrations.Run.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaExceptionalProvider(
            dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
            configure: o => o.ApplicationName = "App",
            engine: MigrationEngine.Postgres,
            connectionString: "Data Source=:memory:",
            runMigration: true));

        Assert.Contains("PostgreSQL", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
