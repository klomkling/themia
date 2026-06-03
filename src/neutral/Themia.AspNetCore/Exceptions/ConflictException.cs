namespace Themia.AspNetCore.Exceptions;

/// <summary>State conflict (e.g. duplicate). Maps to HTTP 409.</summary>
public sealed class ConflictException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
