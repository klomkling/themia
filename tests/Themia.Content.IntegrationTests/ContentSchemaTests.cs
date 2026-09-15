using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Content.IntegrationTests;

public abstract class ContentSchemaTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Migration_ShouldCreateBothTables()
    {
        Assert.True(await fixture.TableExistsAsync("content_pages"));
        Assert.True(await fixture.TableExistsAsync("content_page_revisions"));
    }

    [Fact]
    public async Task Migration_ShouldCreateBothUniqueIndexes()
    {
        Assert.True(await fixture.IndexExistsAsync("ux_content_pages_slug_language"));
        Assert.True(await fixture.IndexExistsAsync("ux_content_page_revisions_page_version"));
    }

    [Fact]
    public void Migration_ShouldRunAgainWithoutError() =>
        ThemiaMigrations.Run(fixture.MigrationAdapter, fixture.ConnectionString, typeof(ContentSchemaMigration).Assembly);

    [Fact]
    public async Task Migration_ShouldRecordItselfInThisAssemblysOwnLedger()
    {
        var ledger = new ThemiaVersionTable(typeof(ContentSchemaMigration).Assembly).TableName;
        Assert.True(await fixture.TableExistsAsync(ledger), $"expected ledger table {ledger}");
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSchemaTests(PostgresContentFixture fixture) : ContentSchemaTests(fixture);

[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentSchemaTests(MySqlContentFixture fixture) : ContentSchemaTests(fixture);

[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentSchemaTests(SqlServerContentFixture fixture) : ContentSchemaTests(fixture);
