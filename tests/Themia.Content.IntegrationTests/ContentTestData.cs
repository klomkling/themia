namespace Themia.Content.IntegrationTests;

internal static class ContentTestData
{
    /// <summary>A slug no other test uses: lowercase hex after a prefix, so it passes the slug rule.</summary>
    public static string NewSlug() => $"p-{Guid.NewGuid():N}";

    public static ContentPageSave Save(
        string slug, string language, int expectedVersion, string markdown = "# Body", string title = "Title",
        bool isPublished = true, string? editorId = "editor-a", string? changeSummary = null) =>
        new(slug, language, title, markdown, isPublished, expectedVersion, changeSummary, editorId);
}
