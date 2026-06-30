using Microsoft.EntityFrameworkCore;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

internal sealed class ExportRunStore(ExportDbContext db) : IExportRunStore
{
    public async Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<ExportRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Tenant-scoped: the standard query filter restricts this to the current tenant's rows.
        return await db.Runs.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters drops the combined tenant+soft-delete EF filter; re-apply soft-delete so
        // the cross-tenant read still never surfaces deleted rows (mirrors EfReadRepository.WithSoftDeleteOnly).
        return await db.Runs
            .IgnoreQueryFilters()
            .Where(r => !r.IsDeleted)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Update(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Detach the failed run so it does not stay tracked as Modified and get re-flushed by the next
            // SaveChanges on this shared context — which would otherwise poison every subsequent run in the
            // cleanup sweep (a persistent failure on one row would abort all later rows).
            db.Entry(run).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken cancellationToken)
    {
        var q = db.Runs.AsNoTracking();
        if (userId is not null)
        {
            q = q.Where(r => r.UserId == userId);
        }

        return await q.OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Same cross-tenant + soft-delete pattern as GetByIdIgnoringTenantAsync. AsNoTracking so the bulk
        // result is not tracked: the cleanup sweep updates each run individually, isolated from the others.
        return await db.Runs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => !r.IsDeleted && r.Status == ExportRunStatus.Succeeded && r.ExpiresAt != null && r.ExpiresAt < now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportRun>> FindStaleRunningAcrossTenantsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        // Same cross-tenant + soft-delete + AsNoTracking pattern as FindExpiredAcrossTenantsAsync.
        return await db.Runs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => !r.IsDeleted && r.Status == ExportRunStatus.Running && r.StartedAt != null && r.StartedAt < cutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
