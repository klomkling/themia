namespace Themia.Content;

/// <summary>A request to create or update one page in one language.</summary>
/// <remarks>
/// <para><b><see cref="ExpectedVersion"/> is required and never nullable.</b> A page that does not exist is
/// version 0, so one number covers create and update, and a caller that omits it compares 0 with the real version
/// and is refused instead of overwriting newer text.</para>
/// <para><b><see cref="Language"/> has no default.</b> A defaulted language let propertiezy's revert write the
/// English body over the Thai row (coord #0130 [3]).</para>
/// </remarks>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language; normalised before it is stored.</param>
/// <param name="Title">The page title.</param>
/// <param name="Markdown">The page body; checked by <see cref="ContentMarkdownRules"/>.</param>
/// <param name="IsPublished">Whether readers are served the page.</param>
/// <param name="ExpectedVersion">0 to create; otherwise the version the editor loaded.</param>
/// <param name="ChangeSummary">An optional note stored on the revision.</param>
/// <param name="EditorId">Who is saving, as the consumer identifies users.</param>
public sealed record ContentPageSave(
    string Slug, string Language, string Title, string Markdown, bool IsPublished,
    int ExpectedVersion, string? ChangeSummary, string? EditorId);

/// <summary>A request to save an old revision's title and body as a new version.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language of the page being reverted.</param>
/// <param name="TargetVersion">The revision whose title and body become the new version.</param>
/// <param name="ExpectedVersion">The version the editor loaded.</param>
/// <param name="ChangeSummary">An optional note; defaults to "Reverted to version N".</param>
/// <param name="EditorId">Who is reverting.</param>
public sealed record ContentPageRevert(
    string Slug, string Language, int TargetVersion, int ExpectedVersion, string? ChangeSummary, string? EditorId);
