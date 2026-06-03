namespace Themia.AspNetCore.Exceptions;

/// <summary>Authenticated but not permitted. Maps to HTTP 403.</summary>
public sealed class ForbiddenException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
