using Dapper;

using Themia.Audit.Migrations;
using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// Engine-agnostic migration assertions, run once per engine by a thin subclass sharing that engine's
/// <see cref="AuditStoreFixture"/> container with the store tests (see that fixture's remarks on why
/// sharing here is safe). Every assertion here checks the schema's <b>shape</b> — column presence,
/// replay-safety — never row count or table emptiness: this container already has rows in it from
/// whichever store test methods have already run.
/// </summary>
public abstract class AuditSchemaMigrationTestsBase(MigrationEngine engine, IAuditDialect dialect, string connectionString)
{
    [Fact]
    public async Task Migration_creates_the_expected_columns()
    {
        // Column presence, not COUNT(*): this table is shared with store tests that may already have
        // written rows by the time this runs, so an emptiness assertion would be order-dependent.
        await using var conn = dialect.CreateConnection(connectionString);
        await conn.OpenAsync();
        var columns = (await conn.QueryAsync<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_name = 'themia_audit_events'"))
            .Select(c => c.ToLowerInvariant())
            .ToHashSet();

        foreach (var column in new[] { "event_uid", "tenant_id", "occurred_at", "data" })
        {
            Assert.Contains(column, columns);
        }
    }

    [Fact]
    public async Task Up_is_replay_safe()
    {
        // Safe to rerun against this shared, already-migrated container for the same three reasons
        // SequencesSchemaMigrationTests relies on (see AuditStoreFixture's remarks): the CREATE TABLE is
        // guarded, ThemiaMigrations.Run holds an exclusive advisory lock for its duration, and xUnit never
        // runs two test classes from the same collection concurrently — so nothing else can observe the
        // ledger row while it is briefly deleted below.
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);

        await using (var conn = dialect.CreateConnection(connectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM themia_version_themia_audit");
        }

        // Must not throw: the table is already there and the ledger no longer remembers creating it.
        ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly);
    }
}
