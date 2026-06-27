using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

internal sealed class ExportRunStore(ExportDbContext db, IDataFilterScope filterScope) : IExportRunStore
{
    public async Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters drops the combined tenant+soft-delete EF filter; re-apply soft-delete so
        // the cross-tenant read still never surfaces deleted rows (mirrors EfReadRepository.WithSoftDeleteOnly).
        using (filterScope.BypassTenantFilter())
        {
            return await db.Runs
                .IgnoreQueryFilters()
                .Where(r => !r.IsDeleted)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Update(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        // Same cross-tenant + soft-delete pattern as GetByIdIgnoringTenantAsync.
        using (filterScope.BypassTenantFilter())
        {
            return await db.Runs
                .IgnoreQueryFilters()
                .Where(r => !r.IsDeleted && r.Status == ExportRunStatus.Succeeded && r.ExpiresAt != null && r.ExpiresAt < now)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
