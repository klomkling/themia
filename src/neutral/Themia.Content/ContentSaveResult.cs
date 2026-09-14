namespace Themia.Content;

/// <summary>How a save or revert ended.</summary>
public enum ContentSaveOutcome
{
    /// <summary>A new version was written.</summary>
    Saved,

    /// <summary>The page is not at the expected version, or a page created at version 0 already exists. Nothing
    /// was written.</summary>
    Conflict,

    /// <summary>A field failed validation. Nothing was written and no connection was opened.</summary>
    Invalid,

    /// <summary>The page, or the revision a revert targets, does not exist. Nothing was written.</summary>
    NotFound,
}

/// <summary>One field that failed validation.</summary>
/// <param name="Field">The JSON field name.</param>
/// <param name="Message">A message for the author.</param>
public sealed record ContentValidationError(string Field, string Message);

/// <summary>The result of a page save or revert.</summary>
public sealed class ContentSaveResult
{
    private ContentSaveResult(
        ContentSaveOutcome outcome, ContentPage? page, int? currentVersion, string? currentUpdatedBy,
        DateTimeOffset? currentUpdatedAt, IReadOnlyList<ContentValidationError> errors)
    {
        Outcome = outcome;
        Page = page;
        CurrentVersion = currentVersion;
        CurrentUpdatedBy = currentUpdatedBy;
        CurrentUpdatedAt = currentUpdatedAt;
        Errors = errors;
    }

    /// <summary>How the operation ended.</summary>
    public ContentSaveOutcome Outcome { get; }

    /// <summary>The page as saved, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Saved"/>.</summary>
    public ContentPage? Page { get; }

    /// <summary>The page's version as it now stands, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Conflict"/>.</summary>
    public int? CurrentVersion { get; }

    /// <summary>Who last saved the page, on a conflict — lets a client that retried after a lost response
    /// recognise its own write.</summary>
    public string? CurrentUpdatedBy { get; }

    /// <summary>When the page was last saved, on a conflict.</summary>
    public DateTimeOffset? CurrentUpdatedAt { get; }

    /// <summary>The validation errors, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Invalid"/>.</summary>
    public IReadOnlyList<ContentValidationError> Errors { get; }

    /// <summary>Whether a new version was written.</summary>
    public bool Succeeded => Outcome == ContentSaveOutcome.Saved;

    /// <summary>A new version was written.</summary>
    /// <param name="page">The page as saved.</param>
    public static ContentSaveResult Saved(ContentPage page) =>
        new(ContentSaveOutcome.Saved, page, null, null, null, Array.Empty<ContentValidationError>());

    /// <summary>The page has moved on from the expected version.</summary>
    /// <param name="currentVersion">The page's current version.</param>
    /// <param name="updatedBy">Who last saved it.</param>
    /// <param name="updatedAt">When it was last saved.</param>
    public static ContentSaveResult Conflict(int currentVersion, string? updatedBy, DateTimeOffset updatedAt) =>
        new(ContentSaveOutcome.Conflict, null, currentVersion, updatedBy, updatedAt, Array.Empty<ContentValidationError>());

    /// <summary>Validation failed.</summary>
    /// <param name="errors">Every failing field.</param>
    public static ContentSaveResult Invalid(IReadOnlyList<ContentValidationError> errors) =>
        new(ContentSaveOutcome.Invalid, null, null, null, null, errors);

    /// <summary>The page or target revision does not exist.</summary>
    public static ContentSaveResult NotFound() =>
        new(ContentSaveOutcome.NotFound, null, null, null, null, Array.Empty<ContentValidationError>());
}
