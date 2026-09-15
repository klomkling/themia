using Microsoft.AspNetCore.Http;

namespace Themia.Content.AspNetCore;

/// <summary>The single map from content outcomes to HTTP. Success is <c>{ data, meta }</c>; errors are RFC 7807.</summary>
internal static class ContentHttpResults
{
    public static IResult Data(object value) => Results.Ok(new { data = value });

    public static IResult List<T>(PagedResult<T> result, int page, int limit) =>
        Results.Ok(new { data = result.Items, meta = new { page, limit, total = result.Total } });

    public static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Content page not found");

    public static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");

    public static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed to manage content pages");

    public static IResult Invalid(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);

#pragma warning disable CS8524 // Unnamed enum values: a new named outcome must still break this switch at compile time.
    public static IResult FromSave(ContentSaveResult result) => result.Outcome switch
    {
        ContentSaveOutcome.Saved => Data(result.Page!),
        ContentSaveOutcome.Invalid => Invalid(result.Errors
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal)),
        ContentSaveOutcome.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Version conflict",
            detail: "The page has been saved since this editor loaded it. Reload it and apply the change again.",
            extensions: new Dictionary<string, object?>
            {
                ["currentVersion"] = result.CurrentVersion,
                ["updatedBy"] = result.CurrentUpdatedBy,
                ["updatedAt"] = result.CurrentUpdatedAt,
            }),
        ContentSaveOutcome.NotFound => NotFound(),
    };
#pragma warning restore CS8524
}
