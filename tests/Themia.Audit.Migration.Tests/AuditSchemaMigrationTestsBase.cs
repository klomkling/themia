using Dapper;

using Themia.Audit.Migrations;
using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Migration.Tests;

/// <summary>
/// Engine-agnostic migration assertions, run once per engine by a thin subclass bound to that engine's
/// dedicated container (see <see cref="AuditMigrationFixture"/>).
/// </summary>
public abstract class AuditSchemaMigrationTestsBase(MigrationEngine engine, IAuditDialect dialect, string connectionString)
{
    [Fact]
    public async Task Creates_the_same_unqualified_table_on_every_engine()
    {
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);

        // Unqualified on purpose: InSchema is dropped on MySQL and the name would then differ per engine.
        await using var conn = dialect.CreateConnection(connectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM themia_audit_events");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Up_is_replay_safe()
    {
        // FluentMigrator's runner is synchronous; there is no RunAsync.
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);

        // Must not throw: the per-assembly version ledger starts empty on every database that predates
        // it, so a rerun against an already-migrated table has to be a no-op, not a crash-looping CREATE.
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);
    }

    [Fact]
    public async Task Every_expected_column_exists()
    {
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);

        await using var conn = dialect.CreateConnection(connectionString);
        await conn.OpenAsync();
        var columns = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'themia_audit_events'"))
            .Select(c => c.ToLowerInvariant())
            .ToHashSet();

        string[] expected =
        [
            "id", "event_uid", "tenant_id", "category", "event_type", "outcome",
            "actor_id", "actor_name", "entity_type", "entity_id", "occurred_at",
            "ip_address", "user_agent", "correlation_id", "reason", "data",
        ];

        foreach (var column in expected)
        {
            Assert.Contains(column, columns);
        }
    }
}
