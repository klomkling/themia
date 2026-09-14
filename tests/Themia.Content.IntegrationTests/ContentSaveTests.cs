using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentSaveTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Create_ShouldWriteVersionOneAndRevisionOne()
    {
        var slug = NewSlug();

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(1, result.Page!.CurrentVersion);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20);
        Assert.Equal(1, revisions.Total);
        Assert.Equal("# One", revisions.Items[0].Markdown);
    }

    [Fact]
    public async Task Update_ShouldAdvanceTheVersionAndWriteARevision()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", changeSummary: "wording"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(2, result.Page!.CurrentVersion);
        Assert.Equal("# Two", result.Page.Markdown);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20);
        Assert.Equal([2, 1], revisions.Items.Select(r => r.Version));
        Assert.Equal("wording", revisions.Items[0].ChangeSummary);
    }

    [Fact]
    public async Task Update_ShouldReturnTheCurrentStateAndWriteNothing_WhenTheExpectedVersionIsStale()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Newer", editorId: "editor-b"));
        var savedAt = fixture.Time.GetUtcNow();

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Stale", editorId: "editor-a"));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal("editor-b", result.CurrentUpdatedBy);
        Assert.Equal(savedAt, result.CurrentUpdatedAt);
        Assert.Equal("# Newer", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Create_ShouldReturnConflictAndWriteNothing_WhenThePageAlreadyExists()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# First"));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 0, "# Second"));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(1, result.CurrentVersion);
        Assert.Equal("# First", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
    }

    [Fact]
    public async Task Update_ShouldReturnNotFound_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.SaveAsync(Save(NewSlug(), "th", 3));
        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task PublishToggle_ShouldWriteARevision()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, isPublished: true));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, isPublished: false));

        Assert.False(result.Page!.IsPublished);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Timestamps_ShouldRoundTripAsTheSameInstant()
    {
        var slug = NewSlug();
        var instant = fixture.Time.GetUtcNow();

        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        var page = (await fixture.Service.GetForEditAsync(slug, "th"))!;
        var revision = (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Items[0];

        Assert.Equal(instant.UtcDateTime, page.CreatedAt.UtcDateTime);
        Assert.Equal(instant.UtcDateTime, page.UpdatedAt.UtcDateTime);
        Assert.Equal(instant.UtcDateTime, revision.CreatedAt.UtcDateTime);
    }

    [Fact]
    public async Task Language_ShouldBeStoredNormalised()
    {
        var slug = NewSlug();

        await fixture.Service.SaveAsync(Save(slug, " EN ", 0));

        var page = await fixture.Service.GetForEditAsync(slug, "en");
        Assert.NotNull(page);
        Assert.Equal("en", page.Language);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSaveTests(PostgresContentFixture fixture) : ContentSaveTests(fixture);
