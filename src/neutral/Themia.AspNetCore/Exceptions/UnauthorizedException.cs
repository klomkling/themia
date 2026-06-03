namespace Themia.AspNetCore.Exceptions;

/// <summary>Not authenticated. Maps to HTTP 401.</summary>
public sealed class UnauthorizedException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
