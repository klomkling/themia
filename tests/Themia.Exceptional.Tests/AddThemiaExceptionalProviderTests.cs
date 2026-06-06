using Microsoft.Extensions.DependencyInjection;
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
            configureRunner: _ => { },
            databaseDisplayName: "SQLite",
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
            configureRunner: _ => { },
            databaseDisplayName: "SQLite",
            connectionString: null,
            runMigration: true));
    }

    [Fact]
    public void RunMigration_Failure_WrapsInInvalidOperationException_WithDatabaseDisplayName()
    {
        var services = new ServiceCollection();

        // configureRunner adds no FluentMigrator provider, so resolving/running the migration
        // fails inside RunMigration's try — exercising the catch that wraps it in an
        // InvalidOperationException naming the engine.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaExceptionalProvider(
            dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
            configure: o => o.ApplicationName = "App",
            configureRunner: _ => { },
            databaseDisplayName: "ConfabulatedEngine",
            connectionString: "Data Source=:memory:",
            runMigration: true));

        Assert.Contains("ConfabulatedEngine", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
