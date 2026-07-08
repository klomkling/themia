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
        // A no-tenant scope leaves it null => a global template.
        PdfTemplateOwnership.ApplyOnCreate(template, tenantContext.CurrentTenantId);

        db.Templates.Add(template);
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var owned = await FindOwnedAsync(template.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateNotFoundException(template.Key);
        // Only content fields are mutable; TenantId/CreatedAt/CreatedBy are never reassigned (change
        // tracking marks only these + last_modified_* as Modified). This is the EF↔Dapper parity guarantee.
        owned.Body = template.Body;
        owned.Name = template.Name;
        owned.Description = template.Description;
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return owned;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var owned = await FindOwnedAsync(id, cancellationToken).ConfigureAwait(false);
        if (owned is null)
        {
            return; // idempotent; a row outside the caller's scope is a no-op, not an error
        }

        db.Templates.Remove(owned); // ThemiaDbContext converts hard delete to soft delete
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Tracked lookup restricted to rows the current scope OWNS (writes only its own rows). Separate from
    // the AsNoTracking public GetAsync because mutate/remove need a tracked entity.
    private async Task<PdfTemplate?> FindOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.Templates.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        return entity is not null && entity.TenantId == tenantContext.CurrentTenantId ? entity : null;
    }

    public async Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var candidates = await db.Templates.AsNoTracking()
            .Where(x => x.Key == key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return PdfTemplateResolver.PreferTenant(candidates) ?? throw new TemplateNotFoundException(key);
    }
}
