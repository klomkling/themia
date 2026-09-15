using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentReadTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task GetPublished_ShouldReturnTheRequestedLanguage()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# English"));

        Assert.Equal("# English", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Markdown);
    }

    [Fact]
    public async Task GetPublished_ShouldFallBack_WhenTheRequestedLanguageIsMissing()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Language);
    }

    [Fact]
    public async Task GetPublished_ShouldFallBack_WhenTheRequestedLanguageIsUnpublished()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# English", isPublished: false));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fr")]
    public async Task GetPublished_ShouldReadAsTheFallback_WhenTheLanguageIsMissingOrUnconfigured(string? language)
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "en", 0));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, language))!.Language);
    }

    [Fact]
    public async Task GetPublished_ShouldReturnNull_WhenNoLanguageIsPublished()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, isPublished: false));

        Assert.Null(await fixture.Service.GetPublishedAsync(slug, "th"));
    }

    [Fact]
    public async Task GetForEdit_ShouldNeverFallBack()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        Assert.Null(await fixture.Service.GetForEditAsync(slug, "en"));
    }

    [Fact]
    public async Task List_ShouldCountEveryPageAndOrderBySlugThenLanguage()
    {
        var before = (await fixture.Service.ListAsync(1, 1)).Total;
        var slug = "a-" + NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "en", 0));

        var first = await fixture.Service.ListAsync(1, 100);

        Assert.Equal(before + 2, first.Total);
        var mine = first.Items.Where(s => s.Slug == slug).Select(s => s.Language).ToArray();
        Assert.Equal(["en", "th"], mine);
    }

    [Fact]
    public async Task Revisions_ShouldPageNewestFirst()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1));
        await fixture.Service.SaveAsync(Save(slug, "th", 2));

        var second = await fixture.Service.GetRevisionsAsync(slug, "th", 2, 2);

        Assert.Equal(3, second.Total);
        Assert.Equal([1], second.Items.Select(r => r.Version));
    }

    [Fact]
    public async Task Revisions_ShouldBeEmpty_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.GetRevisionsAsync(NewSlug(), "th", 1, 20);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetPublished_ShouldNotMatch_WhenTheSlugDiffersOnlyInCaseOrTrailingSpace()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        Assert.Null(await fixture.Service.GetPublishedAsync(slug.ToUpperInvariant(), "th"));
        Assert.Null(await fixture.Service.GetPublishedAsync(slug + " ", "th"));
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentReadTests(PostgresContentFixture fixture) : ContentReadTests(fixture);

[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentReadTests(MySqlContentFixture fixture) : ContentReadTests(fixture);

[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentReadTests(SqlServerContentFixture fixture) : ContentReadTests(fixture);
