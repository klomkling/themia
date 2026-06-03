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
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ErrorCode = errorCode;
        Metadata = metadata;
    }

    /// <summary>Optional machine-readable error code. Consumers define their own values.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Optional extra key/values surfaced as ProblemDetails extensions.
    /// Values should be JSON-serializable (via System.Text.Json). If serialization fails, the
    /// middleware drops the extensions and emits a minimal problem response rather than failing.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; }
}
