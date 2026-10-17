using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

/// <summary>
/// Every test class in this assembly that reads or mutates <see cref="MigrationEngineRegistry"/> belongs
/// here. The registry is process-wide static state and
/// <see cref="AddThemiaExceptionalProviderTests.RunMigration_BeforeEngineIsRegistered_ThrowsNamingThePackage"/>
/// clears it mid-run; xUnit runs classes in different collections in parallel within an assembly, so
/// without this pinning a future class that resolved through the registry would intermittently observe an
/// empty one. Tests inside a single class never run concurrently, so membership here is the whole
/// guarantee.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationEngineRegistryCollection
{
    /// <summary>The collection name, referenced by <see cref="CollectionAttribute"/> on member classes.</summary>
    public const string Name = "MigrationEngineRegistry";
}

[Collection(MigrationEngineRegistryCollection.Name)]
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

    private sealed class RecordingStore : IExceptionStore
    {
        public ExceptionEntry? Last;
        public Task LogAsync(ExceptionEntry e, CancellationToken ct = default) { Last = e; return Task.CompletedTask; }
        public Task<ExceptionEntry?> GetAsync(Guid g, CancellationToken ct = default) => Task.FromResult<ExceptionEntry?>(null);
        public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(new PagedResult<ExceptionEntry>());
        public Task<int> CountAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> ProtectAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HardDeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> PurgeAsync(DateTime o, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void ContainerBuiltSink_StoresCallerIpAndContext_WhileOtherSinksSeeNeither()
    {
        // The path a host takes: resolve the sink and the enricher from the container and attach them to
        // its Serilog logger. The sink is wired to the container's IHttpContextAccessor, so it can read the
        // request while it stores the exception; the other sink on the same logger never gets the data.
        var store = new RecordingStore();
        var services = new ServiceCollection();
        services.AddThemiaExceptionalProvider(
            dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
            configure: o => { o.ApplicationName = "App"; o.CaptureRequestContext = true; },
            engine: MigrationEngine.Postgres,
            runMigration: false);
        services.AddSingleton<IExceptionStore>(store);
        using var sp = services.BuildServiceProvider();

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        http.Request.Headers["User-Agent"] = "Edge";
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var other = new RecordingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With(sp.GetRequiredService<HttpContextEnricher>())
            .WriteTo.Sink(other)
            .WriteTo.Sink(sp.GetRequiredService<ExceptionalSerilogSink>())
            .CreateLogger();

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Equal("203.0.113.9", store.Last!.IpAddress);
        Assert.Contains("Edge", store.Last.RequestContext);
        var routed = Assert.Single(other.Events);
        Assert.False(routed.Properties.ContainsKey("IpAddress"));
        Assert.False(routed.Properties.ContainsKey("RequestContext"));
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

    [Fact]
    public void RunMigration_BeforeEngineIsRegistered_ThrowsNamingThePackage()
    {
        // Unlike the seven Themia.Modules.* and Themia.Scheduling (which migrate later, from
        // InitializeAsync), this call migrates synchronously, right here, when runMigration is true. A
        // custom IExceptionalSqlDialect has no engine package of its own to carry the adapter, so for
        // this path — and only this path — AddThemiaDataMigrationsPostgreSql() must run BEFORE this call,
        // not merely before the host finishes building its container (spec §6).
        MigrationEngineRegistry.Reset();
        try
        {
            var services = new ServiceCollection();

            var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaExceptionalProvider(
                dialect: new SqliteExceptionalDialect("Data Source=:memory:"),
                configure: o => o.ApplicationName = "App",
                engine: MigrationEngine.Postgres,
                connectionString: "Host=localhost;Database=x",
                runMigration: true));

            Assert.Contains("Themia.Data.Migrations.PostgreSql", ex.Message, StringComparison.Ordinal);
            Assert.Contains("AddThemiaDataMigrationsPostgreSql", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        }
    }
}
