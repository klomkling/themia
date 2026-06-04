using System.Collections.Concurrent;
using Themia.Mediator.Abstractions;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Provides process-level distributed locks backed by <see cref="SemaphoreSlim"/>.
/// Consumers can replace this implementation with one backed by Redis or another distributed store.
/// </summary>
/// <remarks>
/// Semaphores are intentionally never removed from the dictionary. Removing on release is racy:
/// a queued waiter can still hold a reference to the old semaphore while a new caller adds a fresh
/// one for the same resource, breaking mutual exclusion. The cost is one <see cref="SemaphoreSlim"/>
/// per distinct resource key (bounded for typical lock key sets). High-cardinality or unbounded key
/// sets should use a distributed/evicting provider instead.
/// </remarks>
public sealed class InMemoryDistributedLockProvider : IDistributedLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <inheritdoc />
    public async Task<IDistributedLock> AcquireAsync(
        string resource,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var semaphore = _locks.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

        if (!acquired)
        {
            throw new TimeoutException($"Failed to acquire lock for resource '{resource}' within '{timeout}'.");
        }

        return new InMemoryDistributedLock(resource, semaphore);
    }

    private sealed class InMemoryDistributedLock : IDistributedLock
    {
        private readonly string _resource;
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public InMemoryDistributedLock(string resource, SemaphoreSlim semaphore)
        {
            _resource = resource;
            _semaphore = semaphore;
        }

        /// <inheritdoc />
        public string Resource => _resource;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
