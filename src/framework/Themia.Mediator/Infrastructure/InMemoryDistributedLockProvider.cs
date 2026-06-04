using System.Collections.Concurrent;
using Themia.Mediator.Abstractions;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Provides process-level distributed locks backed by <see cref="SemaphoreSlim"/>.
/// Consumers can replace this implementation with one backed by Redis or another distributed store.
/// </summary>
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

        return new InMemoryDistributedLock(resource, semaphore, this);
    }

    private void Release(string resource, SemaphoreSlim semaphore)
    {
        semaphore.Release();
        if (semaphore.CurrentCount == 1)
        {
            _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(resource, semaphore));
        }
    }

    private sealed class InMemoryDistributedLock : IDistributedLock
    {
        private readonly string _resource;
        private readonly SemaphoreSlim _semaphore;
        private readonly InMemoryDistributedLockProvider _owner;
        private int _disposed;

        public InMemoryDistributedLock(string resource, SemaphoreSlim semaphore, InMemoryDistributedLockProvider owner)
        {
            _resource = resource;
            _semaphore = semaphore;
            _owner = owner;
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

            _owner.Release(_resource, _semaphore);
            return ValueTask.CompletedTask;
        }
    }
}
