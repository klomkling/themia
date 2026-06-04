namespace Themia.Mediator.Abstractions;

/// <summary>
/// Interface for commands that need to invalidate specific query caches upon successful execution.
/// Implement this interface to specify which query types should have their caches cleared.
/// </summary>
public interface ICacheInvalidator
{
    /// <summary>
    /// Gets the types of queries whose caches should be invalidated when this command succeeds.
    /// </summary>
    /// <returns>An enumerable of query types to invalidate.</returns>
    IEnumerable<Type> GetInvalidatedQueryTypes();
}
