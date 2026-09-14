using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

/// <summary>The seeding pattern the package README documents, exercised against a real engine.</summary>
public abstract class ContentSeedingTests(ContentEngineFixture fixture)
{
    /// <summary>One content change shipped with application code.</summary>
    /// <param name="AppliesToVersion">The version the page must be at before this change; 0 creates it.</param>
    private sealed record ContentChange(string Slug, string Language, string Title, string Markdown, int AppliesToVersion);

    /// <summary>Applies <paramref name="change"/> when the page is at its <see cref="ContentChange.AppliesToVersion"/>,
    /// and does nothing otherwise. Returns whether a version was written.</summary>
    private static async Task<bool> ApplyAsync(IContentPageService service, ContentChange change)
    {
        var page = await service.GetForEditAsync(change.Slug, change.Language);
        if ((page?.CurrentVersion ?? 0) != change.AppliesToVersion)
        {
            return false;
        }

        var result = await service.SaveAsync(new ContentPageSave(
            change.Slug, change.Language, change.Title, change.Markdown, page?.IsPublished ?? true,
            change.AppliesToVersion, "Shipped with application code", "app"));
        return result.Succeeded;
    }

    [Fact]
    public async Task Change_ShouldCreateThePageOnce_WhenRunTwice()
    {
        var create = new ContentChange(NewSlug(), "th", "Terms", "# Terms", AppliesToVersion: 0);

        Assert.True(await ApplyAsync(fixture.Service, create));
        Assert.False(await ApplyAsync(fixture.Service, create));

        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(create.Slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task LaterChange_ShouldApplyOnce_WhenThePageIsAtTheVersionTheEarlierChangeProduced()
    {
        var slug = NewSlug();
        var create = new ContentChange(slug, "th", "Terms", "# Terms v1", AppliesToVersion: 0);
        var reword = new ContentChange(slug, "th", "Terms", "# Terms v2", AppliesToVersion: 1);

        await ApplyAsync(fixture.Service, create);
        Assert.True(await ApplyAsync(fixture.Service, reword));
        Assert.False(await ApplyAsync(fixture.Service, reword));
        Assert.False(await ApplyAsync(fixture.Service, create));

        var page = await fixture.Service.GetForEditAsync(slug, "th");
        Assert.Equal(2, page!.CurrentVersion);
        Assert.Equal("# Terms v2", page.Markdown);
    }

    [Fact]
    public async Task LaterChange_ShouldLeaveThePageAlone_WhenAnEditorHasSavedSince()
    {
        var slug = NewSlug();
        await ApplyAsync(fixture.Service, new ContentChange(slug, "th", "Terms", "# Terms v1", AppliesToVersion: 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Edited by counsel", editorId: "editor"));

        var applied = await ApplyAsync(fixture.Service, new ContentChange(slug, "th", "Terms", "# Terms v2", AppliesToVersion: 1));

        Assert.False(applied);
        Assert.Equal("# Edited by counsel", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSeedingTests(PostgresContentFixture fixture) : ContentSeedingTests(fixture);
