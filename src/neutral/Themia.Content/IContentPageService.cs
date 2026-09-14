namespace Themia.Content;

/// <summary>Reads and writes content pages. Every rule — a save writes a revision, a stale version is refused, revert
/// is a save of an old body — lives here, once, for every engine.</summary>
/// <remarks>Content that ships with application code must also go through <see cref="SaveAsync"/>, from a startup step,
/// never through SQL written against the content tables: those rules exist nowhere else (see the package README).</remarks>
public interface IContentPageService
{
    /// <summary>The published page in <paramref name="language"/>, else in the configured fallback language, else
    /// <see langword="null"/>. A <see langword="null"/> or unconfigured language reads as the fallback.</summary>
    Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default);

    /// <summary>The page in exactly <paramref name="language"/>, published or not. Never falls back: an editor who
    /// opens one language and is shown another saves it over the wrong row.</summary>
    Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default);

    /// <summary>One page of page summaries, ordered by slug then language.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="limit">Items per page, 1 to 100.</param>
    /// <param name="ct">Cancellation.</param>
    Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default);

    /// <summary>One page of a page's revisions, newest first.</summary>
    Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(string slug, string language, int page, int limit, CancellationToken ct = default);

    /// <summary>Creates a page (<see cref="ContentPageSave.ExpectedVersion"/> 0) or updates one at the expected version,
    /// writing a revision either way.</summary>
    Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default);

    /// <summary>Saves the title and body of <see cref="ContentPageRevert.TargetVersion"/> as a new version, keeping the
    /// page's publish state, in one transaction.</summary>
    Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default);
}
