using System.Text.Json.Serialization;

namespace Themia.Content.AspNetCore;

/// <summary>The body of <c>PUT pages/{slug}/{language}</c>. Unknown members are refused, and every member but
/// <c>ChangeSummary</c> is required — including <c>ExpectedVersion</c>: a client that omits it gets 400 instead of a
/// silent 0, so a missing version is a visible client defect rather than a conflict that looks like someone else's
/// save (spec &#167;9).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SavePageRequest(
    [property: JsonRequired] string Title,
    [property: JsonRequired] string Markdown,
    [property: JsonRequired] bool IsPublished,
    [property: JsonRequired] int ExpectedVersion,
    string? ChangeSummary);

/// <summary>The body of <c>POST pages/{slug}/{language}/revert</c>. Unknown members are refused; <c>Version</c> and
/// <c>ExpectedVersion</c> are required, as on <see cref="SavePageRequest"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RevertPageRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] int ExpectedVersion,
    string? ChangeSummary);
