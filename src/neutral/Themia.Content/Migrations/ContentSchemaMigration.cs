using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Content.Migrations;

/// <summary>Creates <c>content_pages</c> and <c>content_page_revisions</c> on PostgreSQL, MySQL and SQL Server.</summary>
/// <remarks>
/// <para><b>Literal table names on every engine, never <c>InSchema(...)</c></b> — FluentMigrator drops the schema on
/// MySQL, so a qualified name means something different per engine (see <c>ChallengeSchemaMigration</c>).</para>
/// <para><b>No tenant column.</b> Content is platform-level for both consumers (coord #0130).</para>
/// <para><b>Two unique indexes are guards, not optimisations.</b> <c>ux_content_pages_slug_language</c> is the only
/// thing that refuses a second create of the same page; <c>ux_content_page_revisions_page_version</c> refuses a second
/// revision with the same version from any writer that is not the service.</para>
/// </remarks>
[Migration(202609140001, "Themia.Content: create content_pages and content_page_revisions")]
public sealed class ContentSchemaMigration : Migration
{
    internal const string PagesTable = "content_pages";
    internal const string RevisionsTable = "content_page_revisions";

    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): each Themia migration assembly has its own ledger and replays once on an
        // existing database. Every object is created in one block, so the anchor table's presence is the whole
        // migration's presence.
        if (Schema.Table(PagesTable).Exists())
        {
            return;
        }

        // MySQL's FluentMigrator generator has no DateTimeOffset, so MySQL stores DATETIME(6) in UTC.
        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Content supports only PostgreSQL, MySQL, and SQL Server. The active database provider is not " +
                "supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(RevisionsTable);
        Delete.Table(PagesTable);
    }

    private void CreateTables(DateTimeType dt)
    {
        var pages = Create.Table(PagesTable)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("slug").AsString(100).NotNullable()
            .WithColumn("language").AsString(35).NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("markdown").AsString(int.MaxValue).NotNullable()
            .WithColumn("current_version").AsInt32().NotNullable()
            .WithColumn("is_published").AsBoolean().NotNullable();
        dt(pages.WithColumn("created_at")).NotNullable();
        dt(pages.WithColumn("updated_at")).NotNullable();
        pages.WithColumn("updated_by").AsString(256).Nullable();

        var revisions = Create.Table(RevisionsTable)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("page_id").AsInt64().NotNullable()
                .ForeignKey("fk_content_page_revisions_page", PagesTable, "id")
            .WithColumn("version").AsInt32().NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("markdown").AsString(int.MaxValue).NotNullable()
            .WithColumn("change_summary").AsString(500).Nullable();
        dt(revisions.WithColumn("created_at")).NotNullable();
        revisions.WithColumn("created_by").AsString(256).Nullable();

        Create.Index("ux_content_pages_slug_language")
            .OnTable(PagesTable)
            .OnColumn("slug").Ascending()
            .OnColumn("language").Ascending()
            .WithOptions().Unique();

        Create.Index("ux_content_page_revisions_page_version")
            .OnTable(RevisionsTable)
            .OnColumn("page_id").Ascending()
            .OnColumn("version").Ascending()
            .WithOptions().Unique();
    }
}
