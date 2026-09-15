using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Content.DependencyInjection;
using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentServiceRegistrationTests
{
    private static void ThaiAndEnglish(ContentOptions options)
    {
        options.Languages.Add("th");
        options.Languages.Add("en");
        options.FallbackLanguage = "th";
    }

    private static ContentOptions Options()
    {
        var options = new ContentOptions();
        ThaiAndEnglish(options);
        return options;
    }

    [Fact]
    public void AddThemiaContent_ShouldThrowImmediately_WhenOptionsAreInvalid() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaContent(_ => { }));

    [Fact]
    public void ResolvingTheService_ShouldNameTheEngineMethods_WhenNoDialectIsRegistered()
    {
        using var provider = new ServiceCollection().AddThemiaContent(ThaiAndEnglish).BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IContentPageService>());
        Assert.Contains("AddThemiaContentPostgres", error.Message);
    }

    [Fact]
    public void ResolvingTheService_ShouldSucceed_WhenTheDialectIsRegisteredAfterTheCore()
    {
        var services = new ServiceCollection().AddThemiaContent(ThaiAndEnglish);
        services.AddSingleton<IContentPageDialect>(new CountingContentDialect());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IContentPageService>());
    }

    [Fact]
    public void ResolvingTheService_ShouldSucceed_WhenTheDialectIsRegisteredBeforeTheCore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContentPageDialect>(new CountingContentDialect());
        services.AddThemiaContent(ThaiAndEnglish);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IContentPageService>());
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnInvalidWithoutOpeningAConnection()
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        var result = await service.SaveAsync(new ContentPageSave("Bad Slug", "th", "T", "# T", true, 0, null, null));

        Assert.Equal(ContentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Fact]
    public async Task RevertAsync_ShouldReturnInvalidWithoutOpeningAConnection()
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        var result = await service.RevertAsync(new ContentPageRevert("terms", "fr", 1, 2, null, null));

        Assert.Equal(ContentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ListAsync_ShouldThrowBeforeConnecting_WhenPagingIsOutOfRange(int page, int limit)
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(page, limit));
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Theory]
    [InlineData("TERMS")]
    [InlineData("terms ")]
    [InlineData(" terms")]
    [InlineData("Terms")]
    public async Task Reads_ShouldReturnNothingAndOpenNoConnection_WhenTheSlugIsNotCanonical(string slug)
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        var published = await service.GetPublishedAsync(slug, "th");
        var forEdit = await service.GetForEditAsync(slug, "th");
        var revisions = await service.GetRevisionsAsync(slug, "th", 1, 20);

        Assert.Null(published);
        Assert.Null(forEdit);
        Assert.Empty(revisions.Items);
        Assert.Equal(0, revisions.Total);
        Assert.Equal(0, dialect.ConnectionsRequested);
    }
}
