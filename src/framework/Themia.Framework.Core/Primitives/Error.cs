namespace Themia.Framework.Core.Primitives;

/// <summary>
/// Represents a domain or application error with an optional message and exception.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Optional human-readable message describing the error.</param>
/// <param name="Exception">Optional exception associated with the error.</param>
public sealed record Error(string Code, string? Message = null, Exception? Exception = null)
{
    /// <summary>
    /// Represents the absence of an error.
    /// </summary>
    public static Error None { get; } = new("None");

    /// <summary>
    /// Provides a readable representation combining code and message.
    /// </summary>
    /// <returns>Error text.</returns>
    public override string ToString() => Message is null ? Code : $"{Code}: {Message}";
}
