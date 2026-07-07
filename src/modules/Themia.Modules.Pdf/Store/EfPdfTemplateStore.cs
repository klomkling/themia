using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.UnitOfWork;

namespace Themia.Modules.Pdf.Store;

/// <summary>EF Core-backed <see cref="IPdfTemplateStore"/>. Writes go through <see cref="EfUnitOfWork"/>
/// so the tenant/global write-asymmetry guard (<c>ValidateTenantWritesAsync</c>) runs on every save.</summary>
internal sealed class EfPdfTemplateStore(
    PdfDbContext db,
    IDataFilterScope filterScope,
    ISqlExceptionInterpreter interpreter,
    ITenantContext tenantContext) : IPdfTemplateStore
{
    // A fresh EfUnitOfWork per save is cheap (wraps the scoped context) and routes writes through
    // ValidateTenantWritesAsync so the tenant/global write asymmetry is enforced.
    private EfUnitOfWork UoW => new(db, filterScope, interpreter);

    public async Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Id == Guid.Empty)
        {
            template.AssignId(Guid.NewGuid());
        }

        // Mirror EfRepository<T,TKey>.AddAsync: a tenant scope creating TenantId=null yields a
        // tenant-owned row (never a global one). PdfDbContext.SaveChanges has no equivalent stamping
        // (ValidateTenantWritesAsync only checks Modified/Deleted entries), so it must happen here.
        if (template.TenantId is null && tenantContext.CurrentTenantId is { } currentTenant)
        {
            template.TenantId = currentTenant;
        }

        db.Templates.Add(template);
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        db.Templates.Update(template);
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await db.Templates.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return; // idempotent delete
        }

        db.Templates.Remove(existing); // ThemiaDbContext converts hard delete to soft delete
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        var candidates = await db.Templates.AsNoTracking()
            .Where(x => x.Key == key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return PdfTemplateResolver.PreferTenant(candidates) ?? throw new TemplateNotFoundException(key);
    }
}
