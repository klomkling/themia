using System.Text.RegularExpressions;

namespace Themia.Content.Internal;

/// <summary>Field rules for saves and reverts. Runs before any connection is opened.</summary>
internal static class ContentPageValidator
{
    public const int MaxSlugLength = 100;
    public const int MaxTitleLength = 200;
    public const int MaxChangeSummaryLength = 500;
    public const int MaxEditorIdLength = 256;

    private static readonly Regex SlugPattern =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxSlugLength && SlugPattern.IsMatch(slug);

    public static IReadOnlyList<ContentValidationError> Validate(ContentPageSave save, ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ContentValidationError>();
        AddKeyErrors(save.Slug, save.Language, options, errors);
        AddTitleErrors(save.Title, errors);

        if (string.IsNullOrWhiteSpace(save.Markdown))
        {
            errors.Add(new ContentValidationError("markdown", "Markdown is required."));
        }
        else
        {
            errors.AddRange(ContentMarkdownRules.Check(save.Markdown).Select(Describe));
        }

        if (save.ExpectedVersion < 0)
        {
            errors.Add(new ContentValidationError(
                "expectedVersion", "Expected version must be 0 for a new page, or the version the editor loaded."));
        }

        AddOptionalErrors(save.ChangeSummary, save.EditorId, errors);
        return errors;
    }

    public static IReadOnlyList<ContentValidationError> Validate(ContentPageRevert revert, ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(revert);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ContentValidationError>();
        AddKeyErrors(revert.Slug, revert.Language, options, errors);

        if (revert.TargetVersion < 1)
        {
            errors.Add(new ContentValidationError("targetVersion", "Target version must be 1 or greater."));
        }

        if (revert.ExpectedVersion < 1)
        {
            errors.Add(new ContentValidationError(
                "expectedVersion", "Expected version must be the version the editor loaded; only an existing page can be reverted."));
        }

        AddOptionalErrors(revert.ChangeSummary, revert.EditorId, errors);
        return errors;
    }

    private static void AddKeyErrors(string? slug, string? language, ContentOptions options, List<ContentValidationError> errors)
    {
        if (!IsValidSlug(slug))
        {
            errors.Add(new ContentValidationError(
                "slug", $"Slug must be lowercase letters and digits separated by single hyphens, at most {MaxSlugLength} characters."));
        }

        if (!options.IsConfigured(ContentLanguage.Normalise(language)))
        {
            errors.Add(new ContentValidationError(
                "language", $"Language must be one of: {string.Join(", ", options.Languages.Select(ContentLanguage.Normalise))}."));
        }
    }

    private static void AddTitleErrors(string? title, List<ContentValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new ContentValidationError("title", "Title is required."));
        }
        else if (title.Length > MaxTitleLength)
        {
            errors.Add(new ContentValidationError("title", $"Title must be at most {MaxTitleLength} characters."));
        }
    }

    private static void AddOptionalErrors(string? changeSummary, string? editorId, List<ContentValidationError> errors)
    {
        if (changeSummary?.Length > MaxChangeSummaryLength)
        {
            errors.Add(new ContentValidationError("changeSummary", $"Change summary must be at most {MaxChangeSummaryLength} characters."));
        }

        if (editorId?.Length > MaxEditorIdLength)
        {
            errors.Add(new ContentValidationError("editorId", $"Editor id must be at most {MaxEditorIdLength} characters."));
        }
    }

#pragma warning disable CS8524 // Unnamed enum values: a new named kind must still break this switch at compile time.
    private static ContentValidationError Describe(ContentMarkdownViolation violation) => violation.Kind switch
    {
        ContentMarkdownViolationKind.RawHtml => new ContentValidationError(
            "markdown",
            "Markdown must not contain raw HTML. Use markdown syntax, or put the markup in a code block to show it as an example."),
        ContentMarkdownViolationKind.DisallowedUrl => new ContentValidationError(
            "markdown",
            $"Links may use only http, https, mailto or tel, or no scheme. Refused: {violation.Detail}"),
    };
#pragma warning restore CS8524
}
