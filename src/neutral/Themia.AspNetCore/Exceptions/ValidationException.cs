using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Input failed validation. Maps to HTTP 400.</summary>
public sealed class ValidationException(
    string propertyName,
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata)
{
    /// <summary>The offending property/field name.</summary>
    public string PropertyName { get; } = propertyName;
}
