using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

internal sealed class ExportScheduleStore(ExportDbContext db, IDataFilterScope filterScope) : IExportScheduleStore
{
    public async Task<ExportSchedule> CreateAsync(ExportSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return schedule;
    }

    public async Task<ExportSchedule?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters drops the combined tenant+soft-delete EF filter; re-apply soft-delete so
        // the cross-tenant read still never surfaces deleted rows (mirrors ExportRunStore).
        using (filterScope.BypassTenantFilter())
        {
            return await db.Schedules
                .IgnoreQueryFilters()
                .Where(s => !s.IsDeleted)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
