using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>
/// Adds <c>identity.users.concurrency_version</c>, the optimistic-concurrency counter behind
/// <c>IVersionedEntity</c>: a stale full-row save of a user (loaded before an erasure, a stamp rotation or a
/// lockout) now fails with <c>ConcurrencyException</c> instead of writing old values back.
/// </summary>
/// <remarks>
/// <b>Cost.</b> <c>NOT NULL DEFAULT 0</c> is a metadata-only change on PostgreSQL 11+. On SQL Server it is
/// online and metadata-only on Enterprise and a size-of-data update (under a schema-modification lock) on
/// Standard, so schedule it for a quiet window on a large <c>identity.users</c>. Existing rows start at 0;
/// no backfill is needed.
/// </remarks>
[Migration(202611060001, "Themia.Identity: add users.concurrency_version bigint (not null, default 0)")]
public sealed class UserConcurrencyVersionMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe, like the other Identity migrations: adopt a column that is already there.
        if (Schema.Schema(SchemaName).Table("users").Column("concurrency_version").Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(() =>
            Alter.Table("users").InSchema(SchemaName)
                .AddColumn("concurrency_version").AsInt64().NotNullable().WithDefaultValue(0));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() =>
        Delete.Column("concurrency_version").FromTable("users").InSchema(SchemaName);
}
