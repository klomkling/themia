namespace Themia.ETax.Withholding.Data.IntegrationTests;

/// <summary>Raw helpers over a database migrated by <see cref="SampleWithholdingCertificatesMigration"/>.</summary>
internal static class WithholdingCertificateDb
{
    public const int Issued = 1;
    public const int Cancelled = 2;
    public const string Json = "{\"a\":1}";
    public const string OtherJson = "{\"a\":2}";

    public static WithholdingCertificateTableMap Map(string table = "withholding_certificates") =>
        new() { Table = table, IssuedStateValue = Issued, CancelledStateValue = Cancelled };

    public static async Task<Guid> InsertAsync(
        TaxDocumentDb db, string payer = "payer-1", int state = Issued, string number = "WHT-2569-000001", string? source = null, int revision = 1)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync($"""
            INSERT INTO withholding_certificates (id, source_event_id, revision, payer_id, document_number, issue_date,
                snapshot_json, snapshot_sha256, snapshot_schema_version, template_version, state, created_at)
            VALUES ('{id}', '{source ?? "src-" + id.ToString("N")}', {revision}, '{payer}', '{number}', DATE '2026-10-14',
                '{Json}', 'abc', 1, '1', {state}, now())
            """);
        return id;
    }
}
