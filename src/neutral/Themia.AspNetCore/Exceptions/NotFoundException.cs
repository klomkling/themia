namespace Themia.AspNetCore.Exceptions;

/// <summary>The requested resource does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
