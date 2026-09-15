using System.Text.Json.Serialization;

namespace Themia.Content.AspNetCore;

/// <summary>The body of <c>PUT pages/{slug}/{language}</c>. Unknown members are refused.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SavePageRequest(string Title, string Markdown, bool IsPublished, int ExpectedVersion, string? ChangeSummary);

/// <summary>The body of <c>POST pages/{slug}/{language}/revert</c>. Unknown members are refused.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RevertPageRequest(int Version, int ExpectedVersion, string? ChangeSummary);
