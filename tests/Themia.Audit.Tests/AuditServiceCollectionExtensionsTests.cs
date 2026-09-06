using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Audit.DependencyInjection;
using Themia.Audit.Http;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_redactor_enricher_and_recorder()
    {
        var services = new ServiceCollection();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
        // AddThemiaAudit's recorder depends on IAuditStore/IAuditDialect, which a provider package
        // (e.g. AddThemiaAuditPostgreSql) normally supplies; substitute fakes here since this project
        // references no provider package.
        services.AddSingleton<IAuditDialect>(new FakeDialect());
        services.AddSingleton<IAuditStore>(new FakeStore());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IAuditRecorder>());
        Assert.NotNull(provider.GetService<Redaction.IAuditRedactor>());
        Assert.NotNull(provider.GetService<AuditHttpEnricher>());
    }

    [Fact]
    public void AddThemiaAudit_rejects_an_unset_engine()
    {
        var services = new ServiceCollection();

        // Must not throw here — only IOptions<AuditOptions>.Value throws (ValidateOnStart, design §11).
        services.AddThemiaAudit(o => { o.ConnectionString = "fake"; /* Engine left Unspecified */ }, runMigration: false);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AuditOptions>>().Value);
    }

    [Fact]
    public void AddThemiaAudit_rejects_an_empty_connection_string()
    {
        var services = new ServiceCollection();

        services.AddThemiaAudit(o => o.Engine = AuditEngine.Postgres, runMigration: false);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AuditOptions>>().Value);
    }

    [Fact]
    public void AddThemiaAudit_does_not_run_migration_when_runMigration_is_false()
    {
        var services = new ServiceCollection();

        // No real database reachable at this connection string; runMigration: false must mean no
        // migration attempt happens, so AddThemiaAudit itself must not throw.
        var exception = Record.Exception(() => services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "not-a-real-connection-string";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false));

        Assert.Null(exception);
    }

    [Fact]
    public void AddThemiaAudit_with_default_runMigration_surfaces_a_migration_failure()
    {
        var services = new ServiceCollection();

        // Malformed connection string fails fast at parse time — no network I/O, deterministic.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "this is not a connection string";
            o.Engine = AuditEngine.Postgres;
        }));

        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeStore : IAuditStore
    {
        public Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
            Task.FromResult(1L);

        public Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDialect : IAuditDialect
    {
        public DbConnection CreateConnection(string connectionString) => new FakeConnection();

        public string InsertSql => throw new NotSupportedException();

        public string SelectPageSql => throw new NotSupportedException();

        public string CountSql => throw new NotSupportedException();

        public string PurgeSql => throw new NotSupportedException();
    }

    private sealed class FakeConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
