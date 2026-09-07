using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Audit.Migrations;

/// <summary>
/// Creates <c>themia_audit_events</c> — one table, identical on every engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unqualified, never <c>InSchema(...)</c>.</b> FluentMigrator drops <c>InSchema(...)</c> on MySQL —
/// there, "schema" and "database" are the same concept — so a schema-qualified name means something
/// different per engine. That divergence is exactly how <c>Themia.Modules.Messaging</c>'s
/// <c>outbox_messages</c> once collided with <c>Themia.Modules.Notifications</c>'s identically-named
/// table on MySQL. One literal table name on every engine removes the class of defect instead of
/// patching one instance of it.
/// </para>
/// <para>
/// <b><c>id</c> is a <c>bigint</c> identity, not the <see cref="AuditEntry.EventUid"/> guid.</b>
/// <c>.PrimaryKey()</c> produces a clustered key on SQL Server; a random guid key would scatter inserts
/// across the whole B-tree and page-split on essentially every write on a table with no purge default
/// (retention is keep-forever). <c>event_uid</c> still gets its own unique index — it is what a
/// dashboard detail route addresses a row by.
/// </para>
/// </remarks>
[Migration(202609060001, "Themia.Audit: create themia_audit_events")]
public sealed class AuditSchemaMigration : Migration
{
    private const string TableName = "themia_audit_events";

    private delegate ICreateTableColumnOptionOrWithColumnSyntax OccurredAtType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // The unsupported-provider guard runs FIRST, before the adopt-if-exists check below. Reversed, a
        // run against an unsupported engine that already had the table would return silently, the ledger
        // would record the migration as applied, and this exception would never fire again.
        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Audit supports only PostgreSQL, MySQL, and SQL Server. The active database " +
                "provider is not supported; add a migration branch for it."));

        // Adopt-if-exists (coord #0078/#0085/#0096): the per-assembly version ledger starts empty on
        // every database that predates it, so Up() runs once against a table that may already be there.
        // An unguarded CREATE crash-loops the host at boot.
        if (Schema.Table(TableName).Exists())
        {
            return;
        }

        // LOCKSTEP: this per-provider list and the guard above are two parallel whitelists that MUST
        // agree. Adding a provider here without adding its prefix to the guard leaves it throwing
        // NotSupportedException; adding it to the guard without a branch here lets it through to a
        // column-type failure. Edit BOTH when adding a provider.
        IfDatabase("postgresql").Delegate(() => CreateSchema(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateSchema(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateSchema(c => c.AsDateTimeOffset()));
    }

    private void CreateSchema(OccurredAtType occurredAt)
    {
        // Adopter-named fields (event_type, actor_id, actor_name, entity_type, entity_id, reason,
        // correlation_id) are sized to AuditEntry's public MaxXLength constants, which is what actually
        // rejects an over-length value on every engine instead of letting MySQL silently truncate it in
        // non-strict mode. ip_address/user_agent are framework-captured and only ever truncated, never
        // rejected, but still bound to the same constants so the column width and the truncation length
        // can never drift apart.
        var table = Create.Table(TableName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("event_uid").AsGuid().NotNullable()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("category").AsInt16().NotNullable()
            .WithColumn("event_type").AsString(AuditEntry.MaxEventTypeLength).NotNullable()
            .WithColumn("outcome").AsInt16().NotNullable()
            .WithColumn("actor_id").AsString(AuditEntry.MaxActorIdLength).Nullable()
            .WithColumn("actor_name").AsString(AuditEntry.MaxActorNameLength).Nullable()
            .WithColumn("entity_type").AsString(AuditEntry.MaxEntityTypeLength).Nullable()
            .WithColumn("entity_id").AsString(AuditEntry.MaxEntityIdLength).Nullable();
        occurredAt(table.WithColumn("occurred_at")).NotNullable();
        table.WithColumn("ip_address").AsString(AuditEntry.MaxIpAddressLength).Nullable();
        table.WithColumn("user_agent").AsString(AuditEntry.MaxUserAgentLength).Nullable();
        table.WithColumn("correlation_id").AsString(AuditEntry.MaxCorrelationIdLength).Nullable();
        table.WithColumn("reason").AsString(AuditEntry.MaxReasonLength).Nullable();
        // AsString(int.MaxValue) on every engine, deliberately not jsonb/JSON: Postgres and MySQL
        // validate JSON on insert and SQL Server does not, and because a later task makes audit writes
        // join the caller's transaction, a malformed payload would roll back the adopter's business
        // write on two engines out of three. See ExceptionLogMigration.cs:55 for the sibling this follows.
        table.WithColumn("data").AsString(int.MaxValue).Nullable();

        // event_uid is the dashboard's detail-route lookup key and the public identifier callers hold;
        // unique so GetAsync's WHERE event_uid = @EventUid is guaranteed at most one row without a LIMIT.
        Create.Index("ux_themia_audit_events_event_uid")
            .OnTable(TableName).OnColumn("event_uid").Ascending()
            .WithOptions().Unique();

        // Index budget — each index below serves exactly one query family (design §5); an index with no
        // query behind it is write cost for nothing on an append-only, keep-forever table.
        // (tenant_id, occurred_at DESC): the tenant timeline — any query filtering TenantId.
        Create.Index("ix_themia_audit_events_tenant")
            .OnTable(TableName)
            .OnColumn("tenant_id").Ascending()
            .OnColumn("occurred_at").Descending();

        // (actor_id, occurred_at DESC): "everything this user did".
        Create.Index("ix_themia_audit_events_actor")
            .OnTable(TableName)
            .OnColumn("actor_id").Ascending()
            .OnColumn("occurred_at").Descending();

        // (entity_type, entity_id, occurred_at DESC): "history of this record".
        Create.Index("ix_themia_audit_events_entity")
            .OnTable(TableName)
            .OnColumn("entity_type").Ascending()
            .OnColumn("entity_id").Ascending()
            .OnColumn("occurred_at").Descending();

        // (occurred_at): the purge scan, and the dashboard's unfiltered default list — TenantId = null
        // there means "no filter", so there is no leading-column predicate the tenant index could serve.
        Create.Index("ix_themia_audit_events_occurred_at")
            .OnTable(TableName).OnColumn("occurred_at").Ascending();
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table(TableName);
}
