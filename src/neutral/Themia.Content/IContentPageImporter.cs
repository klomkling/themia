namespace Themia.Content;

/// <summary>One revision of a page being imported, as the source system recorded it.</summary>
/// <param name="Version">The version this revision created.</param>
/// <param name="Title">The title at that version.</param>
/// <param name="Markdown">The body at that version.</param>
/// <param name="ChangeSummary">The source's change note, if any.</param>
/// <param name="CreatedBy">The source's author id, as a string.</param>
/// <param name="CreatedAt">When the source wrote it.</param>
public sealed record ContentRevisionImport(
    int Version, string Title, string Markdown, string? ChangeSummary, string? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>One page being imported, with its whole history.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language; normalised before it is stored.</param>
/// <param name="Title">The page's current title.</param>
/// <param name="Markdown">The page's current body — what readers are served.</param>
/// <param name="CurrentVersion">The source's current version.</param>
/// <param name="IsPublished">Whether readers are served the page.</param>
/// <param name="CreatedAt">When the source created the page.</param>
/// <param name="UpdatedAt">When the source last saved it.</param>
/// <param name="UpdatedBy">Who last saved it, as a string.</param>
/// <param name="Revisions">Every revision, version 1 through <paramref name="CurrentVersion"/>.</param>
public sealed record ContentPageImport(
    string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy, IReadOnlyList<ContentRevisionImport> Revisions);

/// <summary>How an import ended.</summary>
public enum ContentImportOutcome
{
    /// <summary>Every page and revision was written.</summary>
    Imported,

    /// <summary>The content tables already hold pages. An import is a one-time move, never a merge. Nothing was written.</summary>
    TargetNotEmpty,

    /// <summary>The input broke at least one rule. Nothing was written and no connection was opened.</summary>
    Invalid,
}

/// <summary>A rule an imported page broke.</summary>
public enum ContentImportRule
{
    /// <summary>The same slug and normalised language appear more than once.</summary>
    DuplicateKey,

    /// <summary>The slug fails the slug rule, or the language is not configured.</summary>
    InvalidSlugOrLanguage,

    /// <summary>A title, change summary or author id is longer than its column.</summary>
    FieldTooLong,

    /// <summary>The revisions are not exactly versions 1 through the current version.</summary>
    NonContiguousRevisions,

    /// <summary>The page's title or body differs from its current revision's. Importing would either publish the
    /// revision over the served text or store history that does not contain it. Record the served content as a new
    /// revision in the source first.</summary>
    ContentDiffersFromCurrentRevision,
}

/// <summary>One page that broke one rule.</summary>
/// <param name="Slug">The page's slug as given.</param>
/// <param name="Language">The page's language as given, except for <see cref="ContentImportRule.DuplicateKey"/>,
/// which reports the normalised language.</param>
/// <param name="Rule">The rule it broke.</param>
public sealed record ContentImportViolation(string Slug, string Language, ContentImportRule Rule);

/// <summary>The result of <see cref="IContentPageImporter.ImportAsync"/>.</summary>
public sealed class ContentImportResult
{
    private ContentImportResult(
        ContentImportOutcome outcome, IReadOnlyList<ContentImportViolation> violations, int importedPages,
        int importedRevisions, int pagesFailingCurrentRules, int revisionsFailingCurrentRules)
    {
        Outcome = outcome;
        Violations = violations;
        ImportedPages = importedPages;
        ImportedRevisions = importedRevisions;
        PagesFailingCurrentRules = pagesFailingCurrentRules;
        RevisionsFailingCurrentRules = revisionsFailingCurrentRules;
    }

    /// <summary>How the import ended.</summary>
    public ContentImportOutcome Outcome { get; }

    /// <summary>Every violation, when <see cref="Outcome"/> is <see cref="ContentImportOutcome.Invalid"/>.</summary>
    public IReadOnlyList<ContentImportViolation> Violations { get; }

    /// <summary>Pages written.</summary>
    public int ImportedPages { get; }

    /// <summary>Revisions written.</summary>
    public int ImportedRevisions { get; }

    /// <summary>Pages whose current body would be refused by <see cref="ContentMarkdownRules"/> today. Reported, never
    /// refused: history cannot be edited, and the next save of such a page will be asked to fix it.</summary>
    public int PagesFailingCurrentRules { get; }

    /// <summary>Revisions whose body would be refused by <see cref="ContentMarkdownRules"/> today. Reported, never refused.</summary>
    public int RevisionsFailingCurrentRules { get; }

    /// <summary>Whether everything was written.</summary>
    public bool Succeeded => Outcome == ContentImportOutcome.Imported;

    /// <summary>Everything was written.</summary>
    public static ContentImportResult Imported(int pages, int revisions, int pagesFailingCurrentRules, int revisionsFailingCurrentRules) =>
        new(ContentImportOutcome.Imported, Array.Empty<ContentImportViolation>(), pages, revisions, pagesFailingCurrentRules, revisionsFailingCurrentRules);

    /// <summary>The target already holds pages.</summary>
    public static ContentImportResult TargetNotEmpty() =>
        new(ContentImportOutcome.TargetNotEmpty, Array.Empty<ContentImportViolation>(), 0, 0, 0, 0);

    /// <summary>The input broke rules.</summary>
    public static ContentImportResult Invalid(
        IReadOnlyList<ContentImportViolation> violations, int pagesFailingCurrentRules, int revisionsFailingCurrentRules) =>
        new(ContentImportOutcome.Invalid, violations, 0, 0, pagesFailingCurrentRules, revisionsFailingCurrentRules);
}

/// <summary>A one-time move of pages and their history from another system into Themia.Content's tables.</summary>
/// <remarks>
/// <para>Themia never reads another system's tables: the consumer supplies the rows (see the package README for a
/// recipe). Every rule is checked before anything is written, in one transaction; any violation writes nothing.</para>
/// <para>Version numbers, timestamps, authors and change summaries are preserved; row ids are not.</para>
/// </remarks>
public interface IContentPageImporter
{
    /// <summary>Validates <paramref name="pages"/> and, when every rule holds and the content tables are empty, writes them.</summary>
    Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default);
}
