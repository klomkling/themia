using Themia.Data.Migrations;
using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationEndToEndTests(PostgresFixture postgres)
{
    private static void Migrate(string connectionString) =>
        ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString, typeof(SampleTaxDocumentsMigration).Assembly);

    [Fact]
    public async Task AppMigration_ShouldCreateTheTableAndBothGuards_ThroughTheRealMigrationRunner()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());

        Migrate(db.ConnectionString);

        var id = await db.InsertIssuedAsync();
        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'x' WHERE id = '{id}'");
        await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{id}'");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'fk_tax_documents_replaced_same_seller'"));
    }

    [Fact]
    public async Task AppMigration_ShouldAdoptAnExistingTableWithLegacyRows_AndLeaveThemUntouched()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        var legacy = await db.InsertLegacyAsync(state: TaxDocumentDb.Sent, number: "OLD-7");

        Migrate(db.ConnectionString);

        Assert.Equal("OLD-7", await db.ScalarAsync<string>($"SELECT document_number FROM tax_documents WHERE id = '{legacy}'"));
        Assert.Equal(TaxDocumentDb.Sent, await db.ScalarAsync<int>($"SELECT state FROM tax_documents WHERE id = '{legacy}'"));
        await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{legacy}'");
        await db.ExecuteAsync($"UPDATE tax_documents SET document_number = 'RENUMBERED' WHERE id = '{legacy}'");
        var issued = await db.InsertIssuedAsync(number: "NEW-1");
        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'x' WHERE id = '{issued}'");
    }

    [Fact]
    public async Task AppMigration_ShouldBeReplaySafe_WhenItsLedgerRowIsLost()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        Migrate(db.ConnectionString);
        var id = await db.InsertIssuedAsync();

        // A second full pass over a database that already has the table, the function, both triggers, the index and the constraint.
        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(TaxDocumentDb.Map()).Concat(PostgresTaxDocumentGuardSql.SameSellerKey(TaxDocumentDb.Map())));
        Migrate(db.ConnectionString);

        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'x' WHERE id = '{id}'");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'fk_tax_documents_replaced_same_seller'"));
    }
}
