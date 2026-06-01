using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Requested resource does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
