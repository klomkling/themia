using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore.Tests;

/// <summary>In-memory IExceptionStore for endpoint tests; records the last filter passed to ListAsync.</summary>
internal sealed class FakeExceptionStore : IExceptionStore
{
    private readonly List<ExceptionEntry> _entries;

    public FakeExceptionStore(params ExceptionEntry[] entries) => _entries = entries.ToList();

    public ExceptionFilter? LastFilter { get; private set; }

    public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter filter, CancellationToken cancellationToken = default)
    {
        LastFilter = filter;
        return Task.FromResult(new PagedResult<ExceptionEntry> { Items = _entries, Total = _entries.Count });
    }

    public Task<ExceptionEntry?> GetAsync(Guid guid, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Guid == guid));

    public Task LogAsync(ExceptionEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> CountAsync(ExceptionFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(_entries.Count);
    public Task<bool> ProtectAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> DeleteAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> HardDeleteAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
