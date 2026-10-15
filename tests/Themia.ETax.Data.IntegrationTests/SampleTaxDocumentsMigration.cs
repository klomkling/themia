using FluentMigrator;

namespace Themia.ETax.Data.IntegrationTests;

/// <summary>
/// What an adopting app's migration looks like: create (or adopt) the table, then call the required helpers. It is the ONLY migration in this
/// assembly, so <c>ThemiaMigrations.Run</c> over this assembly runs exactly this.
/// </summary>
[Migration(202610150001, "Sample app: tax_documents with the Themia.ETax guards")]
public sealed class SampleTaxDocumentsMigration : Migration
{
    public override void Up()
    {
        // Adopt a table that already exists (an app that issued documents before Themia.ETax) instead of failing on it.
        if (!Schema.Table("tax_documents").Exists())
        {
            Create.Table("tax_documents")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
                .WithColumn("kind").AsInt32().NotNullable()
                .WithColumn("source_event_id").AsString().NotNullable()
                .WithColumn("revision").AsInt32().NotNullable()
                .WithColumn("seller_id").AsString().NotNullable()
                .WithColumn("document_number").AsString().Nullable()
                .WithColumn("issue_date").AsDate().Nullable()
                .WithColumn("snapshot_json").AsString(int.MaxValue).Nullable()
                .WithColumn("snapshot_sha256").AsString().Nullable()
                .WithColumn("snapshot_schema_version").AsInt32().Nullable()
                .WithColumn("template_version").AsString().Nullable()
                .WithColumn("state").AsInt32().NotNullable()
                .WithColumn("replaced_by_document_id").AsGuid().Nullable();
        }

        var map = TaxDocumentDb.Map();
        TaxDocumentGuards.ApplyImmutability(this, map);
        TaxDocumentGuards.ApplySameSellerReplacementKey(this, map);
    }

    public override void Down() => throw new NotSupportedException("Forward-only.");
}
