namespace Themia.Exceptional;

/// <summary>Persistence API for stored exceptions: rollup-aware logging plus dashboard queries.</summary>
public interface IExceptionStore
{
    /// <summary>Logs an exception, rolling it up into an existing row when an equal hash exists within the rollup period.</summary>
    Task LogAsync(ExceptionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Gets a single stored exception by its <see cref="ExceptionEntry.Guid"/>, or null.</summary>
    Task<ExceptionEntry?> GetAsync(Guid guid, CancellationToken cancellationToken = default);

    /// <summary>Returns a filtered, paged list of stored exceptions plus the total count.</summary>
    Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Counts stored exceptions matching the filter (ignoring paging).</summary>
    Task<int> CountAsync(ExceptionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Marks an exception protected (exempt from purge/soft-delete). Returns true when a row changed.</summary>
    Task<bool> ProtectAsync(Guid guid, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes an exception (sets DeletionDate). Returns true when a row changed.</summary>
    Task<bool> DeleteAsync(Guid guid, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes an exception. Returns true when a row was removed.</summary>
    Task<bool> HardDeleteAsync(Guid guid, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes unprotected rows created before <paramref name="olderThanUtc"/>. Returns the count removed.</summary>
    Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);
}
