using FluentMigrator;

namespace Themia.ETax.Withholding.Data.IntegrationTests;

/// <summary>What an adopting app's migration for withholding certificates looks like: create (or adopt) the table, then call the required helpers.</summary>
[Migration(202610150002, "Sample app: withholding_certificates with the Themia.ETax.Withholding guards")]
public sealed class SampleWithholdingCertificatesMigration : Migration
{
    public override void Up()
    {
        // Adopt a table that already exists instead of failing on it.
        if (!Schema.Table("withholding_certificates").Exists())
        {
            Create.Table("withholding_certificates")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
                .WithColumn("source_event_id").AsString().NotNullable()
                .WithColumn("revision").AsInt32().NotNullable()
                .WithColumn("payer_id").AsString().NotNullable()
                .WithColumn("document_number").AsString().NotNullable()
                .WithColumn("issue_date").AsDate().NotNullable()
                .WithColumn("snapshot_json").AsString(int.MaxValue).NotNullable()
                .WithColumn("snapshot_sha256").AsString().NotNullable()
                .WithColumn("snapshot_schema_version").AsInt32().NotNullable()
                .WithColumn("template_version").AsString().NotNullable()
                .WithColumn("state").AsInt32().NotNullable()
                .WithColumn("replaced_by_certificate_id").AsGuid().Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable();
        }

        var map = WithholdingCertificateDb.Map();
        WithholdingCertificateGuards.ApplyImmutability(this, map);
        WithholdingCertificateGuards.ApplySamePayerReplacementKey(this, map);
        WithholdingCertificateGuards.ApplyIssueKey(this, map);
    }

    public override void Down() => throw new NotSupportedException("Forward-only.");
}
