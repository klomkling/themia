using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentRevertTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Revert_ShouldSaveTheTargetBodyAsANewVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One", title: "One"));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", title: "Two"));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, null, "editor-c"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(3, result.Page!.CurrentVersion);
        Assert.Equal("# One", result.Page.Markdown);
        Assert.Equal("One", result.Page.Title);
        var newest = (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 1)).Items[0];
        Assert.Equal("Reverted to version 1", newest.ChangeSummary);
        Assert.Equal("editor-c", newest.CreatedBy);
    }

    [Fact]
    public async Task Revert_ShouldLeaveTheDefaultLanguageUntouched_WhenRevertingAnotherLanguage()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# EN v1"));
        await fixture.Service.SaveAsync(Save(slug, "en", 1, "# EN v2"));
        var thaiBefore = await fixture.Service.GetForEditAsync(slug, "th");

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "en", 1, 2, null, null));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal("# EN v1", (await fixture.Service.GetForEditAsync(slug, "en"))!.Markdown);
        Assert.Equal(thaiBefore, await fixture.Service.GetForEditAsync(slug, "th"));
        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Revert_ShouldKeepThePublishState()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One", isPublished: true));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", isPublished: false));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, "undo", null));

        Assert.False(result.Page!.IsPublished);
        Assert.Equal("undo", (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 1)).Items[0].ChangeSummary);
    }

    [Fact]
    public async Task Revert_ShouldReturnConflictAndWriteNothing_WhenTheExpectedVersionIsStale()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 1, null, null));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Revert_ShouldReturnNotFound_WhenTheTargetVersionDoesNotExist()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 7, 1, null, null));

        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Revert_ShouldReturnNotFound_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.RevertAsync(new ContentPageRevert(NewSlug(), "th", 1, 1, null, null));
        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentRevertTests(PostgresContentFixture fixture) : ContentRevertTests(fixture);
