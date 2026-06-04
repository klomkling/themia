namespace Themia.Mediator.Abstractions;

/// <summary>
/// Represents an acquired distributed lock that must be released asynchronously.
/// </summary>
public interface IDistributedLock : IAsyncDisposable
{
    /// <summary>
    /// Gets the logical resource name this lock protects.
    /// </summary>
    string Resource { get; }
}
