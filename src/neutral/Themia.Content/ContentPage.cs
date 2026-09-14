namespace Themia.Content;

/// <summary>A content page in one language, as it currently stands.</summary>
/// <param name="Slug">The URL key, lowercase letters and digits separated by single hyphens.</param>
/// <param name="Language">The normalised language.</param>
/// <param name="Title">The page title.</param>
/// <param name="Markdown">The page body.</param>
/// <param name="CurrentVersion">The version of the latest revision; the value an editor sends back as its expected version.</param>
/// <param name="IsPublished">Whether readers are served this page.</param>
/// <param name="CreatedAt">When the page was first saved.</param>
/// <param name="UpdatedAt">When the page was last saved.</param>
/// <param name="UpdatedBy">Who last saved it, as supplied by the consumer.</param>
public sealed record ContentPage(
    string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>A content page without its body, for lists.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The normalised language.</param>
/// <param name="Title">The page title.</param>
/// <param name="CurrentVersion">The version of the latest revision.</param>
/// <param name="IsPublished">Whether readers are served this page.</param>
/// <param name="UpdatedAt">When the page was last saved.</param>
/// <param name="UpdatedBy">Who last saved it.</param>
public sealed record ContentPageSummary(
    string Slug, string Language, string Title, int CurrentVersion, bool IsPublished, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>One immutable revision of a page.</summary>
/// <param name="Version">The version this revision created.</param>
/// <param name="Title">The title at that version.</param>
/// <param name="Markdown">The body at that version.</param>
/// <param name="ChangeSummary">The author's note, if any.</param>
/// <param name="CreatedAt">When the revision was written.</param>
/// <param name="CreatedBy">Who wrote it.</param>
public sealed record ContentPageRevision(
    int Version, string Title, string Markdown, string? ChangeSummary, DateTimeOffset CreatedAt, string? CreatedBy);
