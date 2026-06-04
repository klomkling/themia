using System;
using System.Threading;
using System.Threading.Tasks;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Infrastructure;

public class InMemoryDistributedLockProviderTests
{
    [Fact]
    public async Task AcquireAsync_TwoConcurrentCallsOnSameResource_AreMutuallyExclusive()
    {
        // Arrange
        var provider = new InMemoryDistributedLockProvider();
        const string resource = "test-resource";
        var entered = new SemaphoreSlim(0, 1);
        var release = new SemaphoreSlim(0, 1);
        int concurrentCount = 0;
        int maxConcurrent = 0;

        async Task WorkAsync()
        {
            await using var lock1 = await provider.AcquireAsync(resource, TimeSpan.FromSeconds(5));
            int current = Interlocked.Increment(ref concurrentCount);
            int observed = current;
            // Record peak concurrent holders
            int prev = maxConcurrent;
            while (observed > prev)
            {
                prev = Interlocked.CompareExchange(ref maxConcurrent, observed, prev);
            }
            entered.Release();
            // Hold the lock until signalled
            await release.WaitAsync();
            Interlocked.Decrement(ref concurrentCount);
        }

        // Act: start first task and wait until it holds the lock
        var task1 = Task.Run(WorkAsync);
        await entered.WaitAsync();

        // Start second task while first holds the lock; it should block
        var task2 = Task.Run(WorkAsync);

        // Give task2 time to attempt acquisition (it should be blocked)
        await Task.Delay(100);
        Assert.Equal(0, entered.CurrentCount); // task2 has NOT entered the critical section yet

        // Release the first lock → task2 should now acquire and signal
        release.Release();
        await entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Release task2
        release.Release();
        await Task.WhenAll(task1, task2);

        // Assert: never more than 1 holder at a time
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task AcquireAsync_SameResource_ReusesSameSemaphore()
    {
        // Arrange — acquire and fully release, then acquire again; both should succeed
        var provider = new InMemoryDistributedLockProvider();
        const string resource = "reuse-resource";

        // Act
        await using (var lock1 = await provider.AcquireAsync(resource, TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(resource, lock1.Resource);
        }

        // After release the lock should be acquirable again (semaphore was not corrupted)
        await using var lock2 = await provider.AcquireAsync(resource, TimeSpan.FromSeconds(1));
        Assert.Equal(resource, lock2.Resource);
    }

    [Fact]
    public async Task AcquireAsync_Timeout_ThrowsTimeoutException()
    {
        var provider = new InMemoryDistributedLockProvider();
        const string resource = "timeout-resource";

        await using var held = await provider.AcquireAsync(resource, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(
            () => provider.AcquireAsync(resource, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var provider = new InMemoryDistributedLockProvider();
        var lock1 = await provider.AcquireAsync("idempotent", TimeSpan.FromSeconds(1));

        await lock1.DisposeAsync();
        // Second dispose must be a no-op, not a double-release
        await lock1.DisposeAsync();

        // The resource should be re-acquirable (semaphore released exactly once)
        await using var lock2 = await provider.AcquireAsync("idempotent", TimeSpan.FromSeconds(1));
        Assert.Equal("idempotent", lock2.Resource);
    }
}
