namespace Themia.Mediator.Abstractions;

/// <summary>
/// Provides distributed locks to coordinate work across multiple mediator instances.
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Attempts to acquire a distributed lock for the specified resource.
    /// </summary>
    /// <param name="resource">The logical resource to lock.</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <param name="cancellationToken">Token to cancel the acquisition.</param>
    /// <returns>An acquired lock that must be released via <see cref="IAsyncDisposable.DisposeAsync"/>.</returns>
    Task<IDistributedLock> AcquireAsync(string resource, TimeSpan timeout, CancellationToken cancellationToken = default);
}
