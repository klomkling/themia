using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AdoptionAndSearchPathTests(PostgresFixture postgres)
{
    private static IEnumerable<string> Everything(TaxDocumentTableMap map) =>
        PostgresTaxDocumentGuardSql.Immutability(map).Concat(PostgresTaxDocumentGuardSql.SameSellerKey(map));

    [Fact]
    public async Task Helpers_ShouldBeSafeToRunTwiceAndLeaveOneOfEachObject()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        var map = TaxDocumentDb.Map();

        await db.RunAsync(Everything(map));
        await db.RunAsync(Everything(map));

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_trigger WHERE tgrelid = 'tax_documents'::regclass AND NOT tgisinternal"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_proc WHERE proname LIKE 'tax_documents_etax_%'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'fk_tax_documents_replaced_same_seller'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_tax_documents_id_seller'"));
        var id = await db.InsertIssuedAsync();
        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'x' WHERE id = '{id}'");

        // The later rules survive the replay too: a Cancelled document stays final and its replacement stays write-once.
        var cancelled = await db.InsertIssuedAsync(state: TaxDocumentDb.Failed, number: "C");
        await db.ExecuteAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Cancelled}, replaced_by_document_id = '{id}' WHERE id = '{cancelled}'");
        await db.RefusedAsync($"UPDATE tax_documents SET state = {TaxDocumentDb.Issued} WHERE id = '{cancelled}'");
        await db.RefusedAsync($"UPDATE tax_documents SET replaced_by_document_id = NULL WHERE id = '{cancelled}'");
    }

    [Fact]
    public async Task Immutability_ShouldReplacePermissiveObjectsThatAlreadyExistUnderTheSameNames()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        await db.ExecuteAsync("""
            CREATE FUNCTION tax_documents_etax_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$
            """);
        await db.ExecuteAsync("CREATE TRIGGER trg_tax_documents_etax_guard BEFORE UPDATE ON tax_documents FOR EACH ROW EXECUTE FUNCTION tax_documents_etax_guard()");
        var id = await db.InsertIssuedAsync();
        await db.ExecuteAsync($"UPDATE tax_documents SET document_number = 'free-for-all' WHERE id = '{id}'");

        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(TaxDocumentDb.Map()));

        await db.RefusedAsync($"UPDATE tax_documents SET document_number = 'blocked' WHERE id = '{id}'");
        await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{id}'");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_tax_documents_etax_guard'"));
    }

    [Fact]
    public async Task SameSellerKey_ShouldAdoptAnExistingIndexAndConstraintOfTheSameName()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync();
        await db.ExecuteAsync("CREATE UNIQUE INDEX ux_tax_documents_id_seller ON tax_documents (id, seller_id)");
        await db.ExecuteAsync("""
            ALTER TABLE tax_documents ADD CONSTRAINT fk_tax_documents_replaced_same_seller
              FOREIGN KEY (replaced_by_document_id, seller_id) REFERENCES tax_documents (id, seller_id)
            """);

        await db.RunAsync(PostgresTaxDocumentGuardSql.SameSellerKey(TaxDocumentDb.Map()));

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'fk_tax_documents_replaced_same_seller'"));
    }

    [Fact]
    public async Task Helpers_ShouldFollowTheSearchPath_AndCreateNothingInPublic()
    {
        var connectionString = await postgres.CreateDatabaseAsync(searchPath: "etax_app");
        var db = new TaxDocumentDb(connectionString);
        await db.ExecuteAsync("CREATE SCHEMA etax_app");
        await db.CreateTableAsync();
        Assert.Equal("etax_app", await db.ScalarAsync<string>("SELECT schemaname FROM pg_tables WHERE tablename = 'tax_documents'"));

        await db.RunAsync(Everything(TaxDocumentDb.Map()));

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'etax_app' AND p.proname LIKE 'tax_documents_etax_%'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'tax_documents_etax_%'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ux_tax_documents_id_seller'"));
        var id = await db.InsertIssuedAsync();
        await db.RefusedAsync($"DELETE FROM tax_documents WHERE id = '{id}'");
    }

    [Fact]
    public async Task Helpers_ShouldWorkWhenTheSameTableNameExistsInAnotherSchemaOnTheSearchPath()
    {
        var connectionString = await postgres.CreateDatabaseAsync(searchPath: "etax_a, etax_b");
        var db = new TaxDocumentDb(connectionString);
        await db.ExecuteAsync("CREATE SCHEMA etax_a");
        await db.ExecuteAsync("CREATE SCHEMA etax_b");
        await db.CreateTableAsync("etax_b.tax_documents");      // only in the SECOND schema of the path
        await db.CreateTableAsync("etax_a.other_documents");
        var map = TaxDocumentDb.Map();

        await db.RunAsync(Everything(map));

        var id = await db.InsertIssuedAsync(table: "etax_b.tax_documents");
        await db.RefusedAsync($"DELETE FROM etax_b.tax_documents WHERE id = '{id}'");
    }

    [Fact]
    public async Task Helpers_ShouldBindToTheExplicitSchema_WhenOneIsGiven()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.ExecuteAsync("CREATE SCHEMA etax_explicit");
        await db.CreateTableAsync("etax_explicit.tax_documents");
        var map = TaxDocumentDb.Map(schema: "etax_explicit");

        await db.RunAsync(Everything(map));

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'etax_explicit' AND p.proname LIKE 'tax_documents_etax_%'"));
        var id = await db.InsertIssuedAsync(table: "etax_explicit.tax_documents");
        await db.RefusedAsync($"UPDATE etax_explicit.tax_documents SET kind = 9 WHERE id = '{id}'");
    }

    [Fact]
    public async Task Helpers_ShouldFail_WhenTheTableDoesNotExist()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());

        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(TaxDocumentDb.Map())));
    }

    [Fact]
    public async Task Helpers_ShouldBeReplaySafe_ForTheLongestTableName_BecauseNoGeneratedNameIsTruncated()
    {
        var table = new string('t', TaxDocumentTableMap.MaxTableLength);
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.CreateTableAsync(table);
        var map = TaxDocumentDb.Map(table);

        await db.RunAsync(Everything(map));
        await db.RunAsync(Everything(map));      // the existence check must find the 63-character constraint name

        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM pg_constraint WHERE conname = '{map.ForeignKeyName}'"));
        Assert.Equal(63, map.ForeignKeyName.Length);
        foreach (var name in map.GeneratedNames)
        {
            Assert.True(
                await db.ScalarAsync<long>($"SELECT (SELECT count(*) FROM pg_proc WHERE proname = '{name}') + (SELECT count(*) FROM pg_trigger WHERE tgname = '{name}') + (SELECT count(*) FROM pg_indexes WHERE indexname = '{name}') + (SELECT count(*) FROM pg_constraint WHERE conname = '{name}')") == 1,
                $"{name} should exist exactly once under its full name");
        }

        var id = await db.InsertIssuedAsync(table: table);
        await db.RefusedAsync($"DELETE FROM {table} WHERE id = '{id}'");
    }

    // ---- the state column must fit the configured state values: fail the migration, not the first UPDATE ----

    private async Task<TaxDocumentDb> DbWithStateColumnAsync(string columnType)
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.ExecuteAsync($"CREATE TABLE tax_documents (id uuid PRIMARY KEY, seller_id text NOT NULL, snapshot_json text NULL, state {columnType} NOT NULL, replaced_by_document_id uuid NULL)");
        return db;
    }

    private static TaxDocumentTableMap SmallMap(object sent, object cancelled) => new()
    {
        Table = "tax_documents",
        SentStateValue = sent,
        CancelledStateValue = cancelled,
        ImmutableColumns = ["id", "seller_id", "snapshot_json"],
    };

    [Theory]
    [InlineData("text", 3, 5)]
    [InlineData("varchar(20)", 3, 5)]
    [InlineData("uuid", 3, 5)]
    [InlineData("int", "Sent", "Cancelled")]
    [InlineData("bigint", "Sent", "Cancelled")]
    public async Task Immutability_ShouldFailTheMigration_WhenTheStateColumnCannotHoldTheConfiguredValues(string columnType, object sent, object cancelled)
    {
        var db = await DbWithStateColumnAsync(columnType);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(SmallMap(sent, cancelled))));

        Assert.Contains("state column state", ex.MessageText);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal"));
    }

    [Theory]
    [InlineData("smallint", 3, 5)]
    [InlineData("int", 3, 5)]
    [InlineData("bigint", 3L, 5L)]
    [InlineData("int", TaxDocumentState.Sent, TaxDocumentState.Cancelled)]
    [InlineData("text", "Sent", "Cancelled")]
    [InlineData("varchar(20)", "Sent", "Cancelled")]
    [InlineData("char(9)", "Sent", "Cancelled")]
    public async Task Immutability_ShouldAccept_AColumnThatFitsTheConfiguredValues(string columnType, object sent, object cancelled)
    {
        var db = await DbWithStateColumnAsync(columnType);

        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(SmallMap(sent, cancelled)));

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal"));
    }

    [Fact]
    public async Task Immutability_ShouldAcceptANativeEnumColumn_WithStringValues()
    {
        var db = new TaxDocumentDb(await postgres.CreateDatabaseAsync());
        await db.ExecuteAsync("CREATE TYPE doc_state AS ENUM ('Issued', 'Sent', 'Cancelled')");
        await db.ExecuteAsync("CREATE TABLE tax_documents (id uuid PRIMARY KEY, seller_id text NOT NULL, snapshot_json text NULL, state doc_state NOT NULL, replaced_by_document_id uuid NULL)");

        await db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(SmallMap("Sent", "Cancelled")));

        await db.ExecuteAsync("INSERT INTO tax_documents VALUES (gen_random_uuid(), 's', '{}', 'Sent', NULL)");
        await db.RefusedAsync("UPDATE tax_documents SET state = 'Issued'");
    }

    [Fact]
    public async Task Immutability_ShouldFailTheMigration_WhenTheStateColumnDoesNotExist()
    {
        var db = await DbWithStateColumnAsync("int");
        var map = SmallMap(3, 5) with { StateColumn = "status" };

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.RunAsync(PostgresTaxDocumentGuardSql.Immutability(map)));

        Assert.Contains("not found", ex.MessageText);
    }
}
