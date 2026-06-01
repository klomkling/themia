using System;
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Base type for Themia domain exceptions surfaced as RFC-7807 problem responses.</summary>
public abstract class ThemiaException : Exception
{
    /// <summary>Creates a new <see cref="ThemiaException"/>.</summary>
    protected ThemiaException(
        string message,
        string? errorCode = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Metadata = metadata;
    }

    /// <summary>Optional machine-readable error code. Consumers define their own values.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Optional extra key/values surfaced as ProblemDetails extensions.
    /// Values must be JSON-serializable (via System.Text.Json): they are serialized while
    /// handling the exception, so a non-serializable value throws inside the error path.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; }
}
