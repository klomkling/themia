using System.Text.Json.Serialization;

namespace Themia.Content.AspNetCore;

/// <summary>The body of <c>PUT pages/{slug}/{language}</c>. Unknown members are refused. <c>ExpectedVersion</c> is
/// deliberately not required: an omitted value defaults to 0 (spec &#167;9, coord #0130 [3]), which is the create
/// path's fail-closed default, not a silently-skipped optimistic-concurrency check.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SavePageRequest(
    [property: JsonRequired] string Title,
    [property: JsonRequired] string Markdown,
    [property: JsonRequired] bool IsPublished,
    int ExpectedVersion,
    string? ChangeSummary);

/// <summary>The body of <c>POST pages/{slug}/{language}/revert</c>. Unknown members are refused. <c>ExpectedVersion</c>
/// is deliberately not required; see <see cref="SavePageRequest"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RevertPageRequest([property: JsonRequired] int Version, int ExpectedVersion, string? ChangeSummary);
