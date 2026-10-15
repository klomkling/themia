using Npgsql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.ETax.Withholding.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class WithholdingGuardTests(PostgresFixture postgres)
{
    private const string IntegrityViolation = "23000";
    private const string ForeignKeyViolation = "23503";
    private const string UniqueViolation = "23505";

    private async Task<TaxDocumentDb> MigratedAsync()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        ThemiaMigrations.Run(MigrationEngine.Postgres, db.ConnectionString, typeof(SampleWithholdingCertificatesMigration).Assembly);
        return db;
    }

    [Theory]
    [InlineData("source_event_id = 'other'")]
    [InlineData("revision = 2")]
    [InlineData("payer_id = 'payer-2'")]
    [InlineData("document_number = 'WHT-2569-000002'")]
    [InlineData("issue_date = DATE '2026-10-15'")]
    [InlineData("snapshot_json = '{\"a\":2}'")]
    [InlineData("snapshot_sha256 = 'zzz'")]
    [InlineData("snapshot_schema_version = 2")]
    [InlineData("template_version = '2'")]
    [InlineData("id = gen_random_uuid()")]
    [InlineData("snapshot_json = NULL")]
    public async Task Update_ShouldBeRefused_ForEveryImmutableColumn(string assignment)
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db);

        var ex = await db.RefusedAsync($"UPDATE withholding_certificates SET {assignment} WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
    }

    [Fact]
    public async Task Update_ShouldBeAllowed_ForColumnsThatAreNotGuarded()
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db);

        await db.ExecuteAsync($"UPDATE withholding_certificates SET created_at = now() WHERE id = '{id}'");
    }

    [Fact]
    public async Task Delete_AndTruncate_ShouldBeRefused()
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db);

        Assert.Equal(IntegrityViolation, (await db.RefusedAsync($"DELETE FROM withholding_certificates WHERE id = '{id}'")).SqlState);
        Assert.Equal(IntegrityViolation, (await db.RefusedAsync("TRUNCATE withholding_certificates")).SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM withholding_certificates"));
    }

    [Fact]
    public async Task Issued_ShouldNotBeCancelledWithoutAReplacement()
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db);

        var ex = await db.RefusedAsync($"UPDATE withholding_certificates SET state = {WithholdingCertificateDb.Cancelled} WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal(WithholdingCertificateDb.Issued, await db.ScalarAsync<int>($"SELECT state FROM withholding_certificates WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Issued_ShouldBeCancelledWithAReplacement_InTheSameTransaction_AndThenBeFinal()
    {
        var db = await MigratedAsync();
        var old = await WithholdingCertificateDb.InsertAsync(db, number: "WHT-2569-000001", source: "pay_1");
        var replacement = Guid.NewGuid();

        // The service's order: cancel the old certificate pointing at the new id, then insert the new one. The foreign key is deferred.
        await db.RunAsync(
        [
            "BEGIN",
            $"UPDATE withholding_certificates SET state = {WithholdingCertificateDb.Cancelled}, replaced_by_certificate_id = '{replacement}' WHERE id = '{old}'",
            $"""
            INSERT INTO withholding_certificates (id, source_event_id, revision, payer_id, document_number, issue_date,
                snapshot_json, snapshot_sha256, snapshot_schema_version, template_version, state, created_at)
            VALUES ('{replacement}', 'pay_1', 2, 'payer-1', 'WHT-2569-000002', DATE '2026-10-15', '{WithholdingCertificateDb.OtherJson}', 'abc', 1, '1', 1, now())
            """,
            "COMMIT",
        ]);

        Assert.Equal(WithholdingCertificateDb.Cancelled, await db.ScalarAsync<int>($"SELECT state FROM withholding_certificates WHERE id = '{old}'"));
        Assert.Equal(IntegrityViolation, (await db.RefusedAsync($"UPDATE withholding_certificates SET state = {WithholdingCertificateDb.Issued} WHERE id = '{old}'")).SqlState);
        Assert.Equal(IntegrityViolation, (await db.RefusedAsync($"UPDATE withholding_certificates SET replaced_by_certificate_id = '{Guid.NewGuid()}' WHERE id = '{old}'")).SqlState);
        Assert.Equal(IntegrityViolation, (await db.RefusedAsync($"UPDATE withholding_certificates SET replaced_by_certificate_id = NULL WHERE id = '{old}'")).SqlState);
    }

    [Fact]
    public async Task Replacement_ShouldBeRefusedAtCommit_WhenItBelongsToAnotherPayer()
    {
        var db = await MigratedAsync();
        var old = await WithholdingCertificateDb.InsertAsync(db, payer: "payer-1", number: "A");
        var foreign = await WithholdingCertificateDb.InsertAsync(db, payer: "payer-2", number: "B");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            $"UPDATE withholding_certificates SET state = {WithholdingCertificateDb.Cancelled}, replaced_by_certificate_id = '{foreign}' WHERE id = '{old}'"));

        Assert.Equal(ForeignKeyViolation, ex.SqlState);
        Assert.Equal(WithholdingCertificateDb.Issued, await db.ScalarAsync<int>($"SELECT state FROM withholding_certificates WHERE id = '{old}'"));
    }

    [Fact]
    public async Task Replacement_ShouldBeRefused_WhenTheReplacementRowNeverArrives()
    {
        var db = await MigratedAsync();
        var old = await WithholdingCertificateDb.InsertAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            $"UPDATE withholding_certificates SET state = {WithholdingCertificateDb.Cancelled}, replaced_by_certificate_id = '{Guid.NewGuid()}' WHERE id = '{old}'"));

        Assert.Equal(ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task RefusalMessages_ShouldCarryNoRowData()
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db, number: "SECRET-NUMBER-77");

        var ex = await db.RefusedAsync($"UPDATE withholding_certificates SET document_number = 'x' WHERE id = '{id}'");
        var del = await db.RefusedAsync($"DELETE FROM withholding_certificates WHERE id = '{id}'");

        Assert.DoesNotContain("SECRET-NUMBER-77", ex.MessageText + ex.Detail + ex.Where);
        Assert.DoesNotContain("SECRET-NUMBER-77", del.MessageText + del.Detail + del.Where);
    }

    [Fact]
    public async Task Migration_ShouldBeReplaySafe_WhenItsLedgerRowIsLost()
    {
        var db = await MigratedAsync();
        var id = await WithholdingCertificateDb.InsertAsync(db);
        // The runner keeps a ledger table per assembly; forget what it applied, so the whole migration runs again over the existing objects.
        var ledger = await db.ScalarAsync<string>("SELECT tablename FROM pg_tables WHERE tablename ILIKE '%version%' AND schemaname = 'public'");
        await db.ExecuteAsync($"DELETE FROM \"{ledger}\"");

        // A second full pass over a database that already has the table, the function, both triggers, both indexes and the constraint.
        ThemiaMigrations.Run(MigrationEngine.Postgres, db.ConnectionString, typeof(SampleWithholdingCertificatesMigration).Assembly);

        await db.RefusedAsync($"UPDATE withholding_certificates SET document_number = 'x' WHERE id = '{id}'");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'fk_withholding_certificates_replaced_same_seller'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_withholding_certificates_source_revision'"));
    }

    [Fact]
    public async Task IssueKey_ShouldMakeOneCertificatePerSourceEventAndRevision()
    {
        var db = await MigratedAsync();
        await WithholdingCertificateDb.InsertAsync(db, source: "pay_1", number: "A");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => WithholdingCertificateDb.InsertAsync(db, source: "pay_1", number: "B"));

        Assert.Equal(UniqueViolation, ex.SqlState);
        await WithholdingCertificateDb.InsertAsync(db, source: "pay_1", revision: 2, number: "C");
        await WithholdingCertificateDb.InsertAsync(db, source: "pay_2", number: "D");
    }

    [Fact]
    public async Task IssueKey_ShouldAdoptATableThatAlreadyHasRows_WithoutTouchingThem()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.ExecuteAsync("""
            CREATE TABLE withholding_certificates (
                id uuid PRIMARY KEY, source_event_id text NOT NULL, revision int NOT NULL, payer_id text NOT NULL,
                document_number text NOT NULL, issue_date date NOT NULL, snapshot_json text NOT NULL, snapshot_sha256 text NOT NULL,
                snapshot_schema_version int NOT NULL, template_version text NOT NULL, state int NOT NULL,
                replaced_by_certificate_id uuid NULL, created_at timestamptz NOT NULL)
            """);
        var existing = await WithholdingCertificateDb.InsertAsync(db, number: "OLD-1", source: "pay_old");

        ThemiaMigrations.Run(MigrationEngine.Postgres, db.ConnectionString, typeof(SampleWithholdingCertificatesMigration).Assembly);

        Assert.Equal("OLD-1", await db.ScalarAsync<string>($"SELECT document_number FROM withholding_certificates WHERE id = '{existing}'"));
        await db.RefusedAsync($"UPDATE withholding_certificates SET document_number = 'x' WHERE id = '{existing}'");
        await Assert.ThrowsAsync<PostgresException>(() => WithholdingCertificateDb.InsertAsync(db, source: "pay_old", number: "OLD-2"));
    }
}
