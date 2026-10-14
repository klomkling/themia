using Npgsql;
using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SameSellerKeyTests(PostgresFixture postgres)
{
    private const string ForeignKeyViolation = "23503";

    private async Task<TaxDocumentDb> DbAsync()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        await db.RunAsync(PostgresTaxDocumentGuardSql.SameSellerKey(TaxDocumentDb.Map()));
        return db;
    }

    [Fact]
    public async Task Replacement_ShouldBeAccepted_WhenBothDocumentsBelongToTheSameSeller()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync(seller: "seller-1", number: "A");
        var replacement = await db.InsertIssuedAsync(seller: "seller-1", number: "B");

        await db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = '{replacement}' WHERE id = '{old}'");

        Assert.Equal(replacement, await db.ScalarAsync<Guid>($"SELECT replaced_by_document_id FROM tax_documents WHERE id = '{old}'"));
    }

    [Fact]
    public async Task Replacement_ShouldBeRefused_WhenTheReplacementBelongsToAnotherSeller()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync(seller: "seller-1", number: "A");
        var foreign = await db.InsertIssuedAsync(seller: "seller-2", number: "B");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = '{foreign}' WHERE id = '{old}'"));

        Assert.Equal(ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Replacement_ShouldBeRefused_WhenItPointsAtADocumentThatDoesNotExist()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecuteAsync($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{old}'"));

        Assert.Equal(ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task NoReplacement_ShouldBeAccepted()
    {
        var db = await DbAsync();

        await db.InsertIssuedAsync();
        await db.InsertLegacyAsync();

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM tax_documents"));
    }

    /// <summary>The service cancels the old document (naming the replacement) BEFORE it inserts the replacement, in one transaction.</summary>
    [Fact]
    public async Task Replacement_ShouldBeAcceptedWhenTheOldDocumentIsCancelledBeforeTheReplacementIsInserted_InOneTransaction()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync(seller: "seller-1", number: "A");
        var replacementId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await new NpgsqlCommand($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, replaced_by_document_id = '{replacementId}' WHERE id = '{old}'", connection, tx).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"INSERT INTO tax_documents (id, kind, source_event_id, revision, seller_id, state) VALUES ('{replacementId}', 1, 'src', 2, 'seller-1', 1)", connection, tx).ExecuteNonQueryAsync();
        await tx.CommitAsync();

        Assert.Equal(TaxDocumentDb.Cancelled, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{old}'"));
    }

    [Fact]
    public async Task Replacement_ShouldBeRefusedAtCommit_WhenTheReplacementIsNeverInserted()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync();

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await new NpgsqlCommand($"UPDATE tax_documents SET replaced_by_document_id = gen_random_uuid() WHERE id = '{old}'", connection, tx).ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal(ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Replacement_ShouldBeRefusedAtCommit_WhenTheLateInsertedReplacementIsAnotherSellers()
    {
        var db = await DbAsync();
        var old = await db.InsertIssuedAsync(seller: "seller-1");
        var replacementId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await new NpgsqlCommand($"UPDATE tax_documents SET replaced_by_document_id = '{replacementId}' WHERE id = '{old}'", connection, tx).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"INSERT INTO tax_documents (id, kind, source_event_id, revision, seller_id, state) VALUES ('{replacementId}', 1, 'src', 2, 'seller-2', 1)", connection, tx).ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal(ForeignKeyViolation, ex.SqlState);
    }
}
