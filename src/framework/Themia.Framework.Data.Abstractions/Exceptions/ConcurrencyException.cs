namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Thrown when a single-entity write (update or delete) affects no rows — the target row does not exist,
/// was concurrently deleted, or is outside the caller's tenant scope. Surfaces a failed write that would
/// otherwise be silently lost, giving both data layers the same optimistic-concurrency contract.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public ConcurrencyException(string message) : base(message) { }

    /// <summary>Creates the exception with a descriptive message and the underlying cause.</summary>
    public ConcurrencyException(string message, Exception innerException) : base(message, innerException) { }
}
