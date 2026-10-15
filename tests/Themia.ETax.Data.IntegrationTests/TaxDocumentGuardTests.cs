using Npgsql;
using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TaxDocumentGuardTests(PostgresFixture postgres)
{
    private const string IntegrityViolation = "23000";

    private async Task<TaxDocumentDb> GuardedDbAsync()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(TaxDocumentDb.Map()));
        return db;
    }

    // ---- immutable columns ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kind = 2")]
    [InlineData("source_event_id = 'other'")]
    [InlineData("revision = 2")]
    [InlineData("seller_id = 'seller-2'")]
    [InlineData("document_number = 'TAX-2'")]
    [InlineData("issue_date = DATE '2026-10-15'")]
    [InlineData("snapshot_json = '{\"a\":2}'")]
    [InlineData("snapshot_sha256 = 'zzz'")]
    [InlineData("snapshot_schema_version = 2")]
    [InlineData("template_version = '2'")]
    [InlineData("id = gen_random_uuid()")]
    public async Task Update_ShouldBeRefused_ForEveryImmutableColumnOfAnIssuedDocument(string assignment)
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync();

        var ex = await db.RefusedAsync($"UPDATE tax_documents SET {assignment} WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
    }

    [Fact]
    public async Task Update_ShouldBeRefused_WhenTheSnapshotIsSetToNull_BecauseTheGuardReadsTheOldRow()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync();

        var ex = await db.RefusedAsync($"UPDATE tax_documents SET snapshot_json = NULL WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal("{\"a\":1}", await db.ScalarAsync<string>($"SELECT snapshot_json FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldBeRefused_WhenSettingAllSnapshotColumnsToNullAtOnce()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync();

        await db.RefusedAsync($"UPDATE tax_documents SET snapshot_json = NULL, snapshot_sha256 = NULL, snapshot_schema_version = NULL, template_version = NULL WHERE id = '{id}'");
    }

    [Fact]
    public async Task Update_ShouldBeAllowed_ForLifecycleColumns()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync();

        await db.ExecuteAsync($"UPDATE tax_documents SET state = 2, attempts = 3, buyer_email = 'x@example.test' WHERE id = '{id}'");

        Assert.Equal(2, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task RefusalMessages_ShouldCarryNoRowData()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(number: "SECRET-NUMBER-77");

        var ex = await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'x' WHERE id = '{id}'");
        var del = await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{id}'");

        Assert.DoesNotContain("SECRET-NUMBER-77", ex.MessageText + ex.Detail + ex.Where);
        Assert.DoesNotContain("SECRET-NUMBER-77", del.MessageText + del.Detail + del.Where);
    }

    // ---- delete and truncate -------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ShouldBeRefused_ForADocumentWithASnapshot()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync();

        var ex = await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    [Fact]
    public async Task Delete_ShouldBeRefused_ForEveryRowIncludingLegacyRowsWithoutASnapshot()
    {
        var db = await GuardedDbAsync();
        var legacy = await db.InsertLegacyAsync();

        var ex = await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{legacy}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    [Fact]
    public async Task Delete_ShouldBeRefused_ForASetOfRowsWhenAnyOfThemIsLegacy()
    {
        var db = await GuardedDbAsync();
        await db.InsertIssuedAsync(number: "A");
        await db.InsertLegacyAsync(number: "B");

        await db.RefusedAsync("DELETE FROM tax_documents");

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    [Fact]
    public async Task Truncate_ShouldBeRefused()
    {
        var db = await GuardedDbAsync();
        await db.InsertIssuedAsync();

        var ex = await db.RefusedAsync("TRUNCATE tax_documents");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    // ---- leaving Sent --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaxDocumentDb.Failed)]
    [InlineData(TaxDocumentDb.Issued)]
    [InlineData(2)]
    public async Task Update_ShouldRefuseLeavingSent_ForAnythingButACancellationWithAReplacement(int target)
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Sent);

        await db.RefusedAsync($"UPDATE tax_documents SET state = {target} WHERE id = '{id}'");
    }

    [Fact]
    public async Task Update_ShouldRefuseCancellingASentDocument_WithoutAReplacementId()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Sent);

        await db.RefusedAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled} WHERE id = '{id}'");
    }

    [Fact]
    public async Task Update_ShouldAllowCancellingASentDocument_WhenItNamesItsReplacement()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Sent);

        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");

        Assert.Equal(TaxDocumentDb.Cancelled, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldAllowCancellingAnUnsentDocument_WithoutAReplacementId()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Failed);

        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled} WHERE id = '{id}'");

        Assert.Equal(TaxDocumentDb.Cancelled, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{id}'"));
    }

    // ---- Cancelled is final; the replacement is write-once ----------------------------------------------------------

    private static async Task<Guid> CancelledAsync(TaxDocumentDb db)
    {
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Sent);
        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");
        return id;
    }

    [Theory]
    [InlineData(TaxDocumentDb.Issued)]
    [InlineData(2)]
    [InlineData(TaxDocumentDb.Sent)]
    [InlineData(TaxDocumentDb.Failed)]
    public async Task Update_ShouldRefuseLeavingCancelled_ForAnyState(int target)
    {
        var db = await GuardedDbAsync();
        var id = await CancelledAsync(db);

        var ex = await db.RefusedAsync($"UPDATE tax_documents SET state = {target} WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, ex.SqlState);
        Assert.Equal(TaxDocumentDb.Cancelled, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldRefuseResurrectingAnUnsentCancelledDocument()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Failed);
        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled} WHERE id = '{id}'");

        await db.RefusedAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Issued} WHERE id = '{id}'");
    }

    [Fact]
    public async Task Update_ShouldAllowOtherLifecycleColumns_WhileTheDocumentStaysCancelled()
    {
        var db = await GuardedDbAsync();
        var id = await CancelledAsync(db);

        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, attempts = 9, buyer_email = 'x@example.test' WHERE id = '{id}'");

        Assert.Equal(9, await db.ScalarAsync<int>($"SELECT attempts FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldBeUnaffected_ForALegacyCancelledRowWithoutASnapshot()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertLegacyAsync(state: TaxDocumentDb.Cancelled);

        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Issued} WHERE id = '{id}'");

        Assert.Equal(TaxDocumentDb.Issued, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldRefuseChangingTheReplacement_OnceItIsSet()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Failed);
        var first = Guid.NewGuid();
        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = '{first}' WHERE id = '{id}'");

        var other = await db.RefusedAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");
        await db.RefusedAsync($"UPDATE tax_documents SET replaced_by_document_id = NULL WHERE id = '{id}'");

        Assert.Equal(IntegrityViolation, other.SqlState);
        Assert.Equal(first, await db.ScalarAsync<Guid>($"SELECT replaced_by_document_id FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldAcceptRewritingTheSameReplacement_AndSettingItWhileItIsStillNull()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(state: TaxDocumentDb.Failed);
        var replacement = Guid.NewGuid();

        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = '{replacement}' WHERE id = '{id}'");     // NULL -> value
        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = '{replacement}', attempts = 1 WHERE id = '{id}'"); // same value

        Assert.Equal(replacement, await db.ScalarAsync<Guid>($"SELECT replaced_by_document_id FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldRefuseRepointingTheReplacementOfACancelledSentDocument()
    {
        var db = await GuardedDbAsync();
        var id = await CancelledAsync(db);      // the legitimate Sent -> Cancelled with a replacement still worked above

        await db.RefusedAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");
    }

    [Fact]
    public async Task Update_ShouldLeaveTheReplacementOfALegacyRowFree()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertLegacyAsync(state: TaxDocumentDb.Sent);
        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");

        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");
    }

    [Fact]
    public async Task NewRefusals_ShouldCarryNoRowData()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertIssuedAsync(number: "SECRET-NUMBER-88", state: TaxDocumentDb.Sent);
        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");

        var resurrect = await db.RefusedAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Issued} WHERE id = '{id}'");
        var repoint = await db.RefusedAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{id}'");

        Assert.DoesNotContain("SECRET-NUMBER-88", resurrect.MessageText + resurrect.Detail + resurrect.Where);
        Assert.DoesNotContain("SECRET-NUMBER-88", repoint.MessageText + repoint.Detail + repoint.Where);
    }

    // ---- legacy rows ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ShouldBeAllowed_ForALegacyRowWithoutASnapshot()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertLegacyAsync(state: TaxDocumentDb.Sent);

        await db.ExecuteAsync($"UPDATE tax_documents SET document_number = 'RENUMBERED', state = {TaxDocumentDb.Failed} WHERE id = '{id}'");

        Assert.Equal("RENUMBERED", await db.ScalarAsync<string>($"SELECT document_number FROM tax_documents WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Update_ShouldGuardALegacyRowFromTheMomentItGainsASnapshot()
    {
        var db = await GuardedDbAsync();
        var id = await db.InsertLegacyAsync();
        await db.ExecuteAsync($"UPDATE tax_documents SET snapshot_json = '{{}}', snapshot_sha256 = 'h', snapshot_schema_version = 1, template_version = '1' WHERE id = '{id}'");

        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'CHANGED' WHERE id = '{id}'");
    }

    // ---- other rows are unaffected ----------------------------------------------------------------------------------

    [Fact]
    public async Task Insert_ShouldBeUnaffected()
    {
        var db = await GuardedDbAsync();

        await db.InsertIssuedAsync(number: "A");
        await db.InsertLegacyAsync(number: "B");

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    [Fact]
    public async Task Guard_ShouldWorkForStringStateValues_AndPascalCaseNames()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.ExecuteAsync("""
            CREATE TABLE "OpsTaxDocuments" ("Id" uuid PRIMARY KEY, "SellerId" text NOT NULL, "SnapshotJson" text NULL,
                                            "DocumentNumber" text NULL, "Status" text NOT NULL, "ReplacedByDocumentId" uuid NULL)
            """);
        var map = new TaxDocumentTableMap
        {
            Table = "OpsTaxDocuments",
            IdColumn = "Id",
            SellerIdColumn = "SellerId",
            StateColumn = "Status",
            ReplacedByColumn = "ReplacedByDocumentId",
            SnapshotJsonColumn = "SnapshotJson",
            ImmutableColumns = ["Id", "SellerId", "SnapshotJson", "DocumentNumber"],
            SentStateValue = "Sent",
            CancelledStateValue = "Cancelled",
        };
        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(map));
        var id = Guid.NewGuid();
        await db.ExecuteAsync("INSERT INTO \"OpsTaxDocuments\" VALUES ('" + id + "', 's', '{}', 'N-1', 'Sent', NULL)");

        await db.RefusedAsync($"""UPDATE "OpsTaxDocuments" SET "DocumentNumber" = 'x' WHERE "Id" = '{id}'""");
        await db.RefusedAsync($"""UPDATE "OpsTaxDocuments" SET "Status" = 'Failed' WHERE "Id" = '{id}'""");
        await db.ExecuteAsync($"""UPDATE "OpsTaxDocuments" SET "Status" = 'Cancelled', "ReplacedByDocumentId" = gen_random_uuid() WHERE "Id" = '{id}'""");
    }
}
