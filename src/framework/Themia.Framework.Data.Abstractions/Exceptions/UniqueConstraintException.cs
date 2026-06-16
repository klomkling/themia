namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Thrown when a write violates a unique or primary-key constraint — a row with the same key (or unique
/// column combination) already exists. Surfaces the engine-specific duplicate-key error as one typed
/// exception across both data layers (EF Core and Dapper) and all engines, so services can use
/// "insert-with-unique-key + catch" as a concurrency-safe compare-and-set.
/// </summary>
public sealed class UniqueConstraintException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public UniqueConstraintException(string message) : base(message) { }

    /// <summary>Creates the exception with a descriptive message and the underlying cause.</summary>
    public UniqueConstraintException(string message, Exception innerException) : base(message, innerException) { }
}
