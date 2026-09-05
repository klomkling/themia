using FluentMigrator;

using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Framework.Data.Sequences.Migrations;

/// <summary>Creates <c>themia_sequences</c>, the counter table behind <see cref="ISequenceProvider"/>.</summary>
/// <remarks>
/// Unqualified (default schema), matching the other framework-level tables — <c>themia_version_*</c> and
/// <c>data_protection_keys</c> — so a consumer with a non-default <c>search_path</c> does not end up with
/// schema and runtime pointing at different places (coord #0088).
/// </remarks>
[Migration(202609050001, "Themia.Sequences: create themia_sequences")]
public sealed class SequencesSchemaMigration : Migration
{
    private const string TableName = "themia_sequences";

    /// <inheritdoc />
    public override void Up()
    {
        // The unsupported-provider guard runs FIRST, before the adopt-if-exists check. Reversed, a run
        // against an unsupported engine that happened to already have a themia_sequences table would
        // return silently, the ledger would record the migration as applied, and this exception would
        // never fire -- leaving the runtime to issue the wrong dialect's SQL instead of failing at
        // migrate time, which is the whole point of the guard.
        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia sequences support only PostgreSQL, MySQL and SQL Server. The active database "
                + "provider is not supported; add a migration branch for it."));

        // Adopt-if-exists, per coord #0078/#0085/#0096: the per-assembly version ledger starts empty on
        // every database that predates it, so this runs once against a table that may already be there.
        // An unguarded CREATE fails and crash-loops the host at boot.
        if (Schema.Table(TableName).Exists()) return;

        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(() =>
            Create.Table(TableName)
                // Width bound to TenantId.MaxLength, not a bare literal: if the two ever drift apart,
                // MySQL truncates tenant_id silently and two tenants share one counter -- the same hazard
                // the sequence_key guard below exists to prevent.
                // NOT NULL with '' for host-level: TenantId is nullable throughout Themia, but no engine
                // permits a NULL column in a primary key. The alternative -- a surrogate key plus UNIQUE
                // over a nullable column -- has engine-divergent NULL semantics (PostgreSQL admits many
                // NULL rows, SQL Server one), which would silently allow two host-level rows for one key
                // and therefore two allocators.
                .WithColumn("tenant_id").AsString(TenantId.MaxLength).NotNullable().WithDefaultValue(string.Empty)
                // 100 must agree with SequenceProvider.MaxSequenceKeyLength, which is what actually
                // rejects an over-length key on every engine instead of letting MySQL silently truncate it.
                .WithColumn("sequence_key").AsString(SequenceProvider.MaxSequenceKeyLength).NotNullable()
                .WithColumn("next_value").AsInt64().NotNullable());

        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(() =>
            Create.PrimaryKey("pk_themia_sequences").OnTable(TableName)
                .Columns("tenant_id", "sequence_key"));

    }

    /// <inheritdoc />
    public override void Down() => Delete.Table(TableName);
}
