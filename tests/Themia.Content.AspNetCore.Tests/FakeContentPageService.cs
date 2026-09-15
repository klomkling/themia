namespace Themia.Content.AspNetCore.Tests;

/// <summary>Records what the endpoints asked for and returns canned answers. Tests HTTP mapping only; the service's
/// behaviour is tested against real engines in Themia.Content.IntegrationTests.</summary>
internal sealed class FakeContentPageService : IContentPageService
{
    public int Calls { get; private set; }
    public ContentPage? Page { get; set; }
    public (string Slug, string? Language)? LastPublishedRequest { get; private set; }
    public ContentPageSave? LastSave { get; private set; }
    public ContentPageRevert? LastRevert { get; private set; }
    public (int Page, int Limit)? LastListRequest { get; private set; }
    public ContentSaveResult SaveResult { get; set; } = ContentSaveResult.NotFound();
    public PagedResult<ContentPageSummary> Summaries { get; set; } = new();
    public PagedResult<ContentPageRevision> Revisions { get; set; } = new();

    public static ContentPage SamplePage(string slug = "terms", string language = "th") =>
        new(slug, language, "Terms", "# Terms", 3, true,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), "editor-a");

    public Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default)
    {
        Calls++;
        LastPublishedRequest = (slug, language);
        return Task.FromResult(Page);
    }

    public Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Page);
    }

    public Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default)
    {
        Calls++;
        LastListRequest = (page, limit);
        return Task.FromResult(Summaries);
    }

    public Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(string slug, string language, int page, int limit, CancellationToken ct = default)
    {
        Calls++;
        LastListRequest = (page, limit);
        return Task.FromResult(Revisions);
    }

    public Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default)
    {
        Calls++;
        LastSave = save;
        return Task.FromResult(SaveResult);
    }

    public Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default)
    {
        Calls++;
        LastRevert = revert;
        return Task.FromResult(SaveResult);
    }
}
