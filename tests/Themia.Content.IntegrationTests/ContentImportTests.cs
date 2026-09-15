using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentImportTests(ContentEngineFixture fixture)
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 9, 12, 5, 30, 0, TimeSpan.FromHours(7));

    private static ContentPageImport TwoVersionPage(string slug) =>
        new(slug, "EN", "Privacy v2", "# Privacy v2", 2, true, Created, Updated, "4d9e1b2c-0000-0000-0000-000000000001",
        [
            new ContentRevisionImport(1, "Privacy", "# Privacy v1", null, "4d9e1b2c-0000-0000-0000-000000000001", Created),
            new ContentRevisionImport(2, "Privacy v2", "# Privacy v2", "counsel wording", "4d9e1b2c-0000-0000-0000-000000000002", Updated),
        ]);

    [Fact]
    public async Task Import_ShouldPreserveVersionsTimestampsAndAuthors()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        Assert.Equal(ContentImportOutcome.Imported, result.Outcome);
        Assert.Equal((1, 2), (result.ImportedPages, result.ImportedRevisions));
        var page = (await fixture.Service.GetForEditAsync(slug, "en"))!;
        Assert.Equal(2, page.CurrentVersion);
        Assert.Equal(Created.UtcDateTime, page.CreatedAt.UtcDateTime);
        Assert.Equal(Updated.UtcDateTime, page.UpdatedAt.UtcDateTime);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "en", 1, 20);
        Assert.Equal([2, 1], revisions.Items.Select(r => r.Version));
        Assert.Equal("counsel wording", revisions.Items[0].ChangeSummary);
        Assert.Equal("4d9e1b2c-0000-0000-0000-000000000002", revisions.Items[0].CreatedBy);
    }

    [Fact]
    public async Task ImportedPage_ShouldBeEditableAtItsImportedVersion()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        var saved = await fixture.Service.SaveAsync(Save(slug, "en", 2, "# Privacy v3"));

        Assert.Equal(ContentSaveOutcome.Saved, saved.Outcome);
        Assert.Equal(3, saved.Page!.CurrentVersion);
    }

    [Fact]
    public async Task Import_ShouldWriteNothing_WhenAnyPageAlreadyExists()
    {
        await fixture.ClearContentAsync();
        await fixture.Service.SaveAsync(Save(NewSlug(), "th", 0));
        var slug = NewSlug();

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        Assert.Equal(ContentImportOutcome.TargetNotEmpty, result.Outcome);
        Assert.Null(await fixture.Service.GetForEditAsync(slug, "en"));
    }

    [Fact]
    public async Task Import_ShouldWriteNothing_WhenServedContentIsInNoRevision()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        var diverged = new ContentPageImport(slug, "th", "Terms", "# Counsel text", 1, true, Created, Updated, null,
            [new ContentRevisionImport(1, "Terms", "# Placeholder", null, null, Created)]);

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(NewSlug()), diverged]);

        Assert.Equal(ContentImportOutcome.Invalid, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.Equal((slug, ContentImportRule.ContentDiffersFromCurrentRevision), (violation.Slug, violation.Rule));
        Assert.Equal(0, (await fixture.Service.ListAsync(1, 1)).Total);
    }

    [Fact]
    public async Task Import_ShouldReportButNotRefuse_HistoryThatTodaysRulesWouldRefuse()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        var page = new ContentPageImport(slug, "th", "T", "# T2", 2, true, Created, Updated, null,
        [
            new ContentRevisionImport(1, "T", "old <b>markup</b>", null, null, Created),
            new ContentRevisionImport(2, "T", "# T2", null, null, Updated),
        ]);

        var result = await fixture.Importer.ImportAsync([page]);

        Assert.Equal(ContentImportOutcome.Imported, result.Outcome);
        Assert.Equal(1, result.RevisionsFailingCurrentRules);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentImportTests(PostgresContentFixture fixture) : ContentImportTests(fixture);

[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentImportTests(MySqlContentFixture fixture) : ContentImportTests(fixture);

[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentImportTests(SqlServerContentFixture fixture) : ContentImportTests(fixture);
