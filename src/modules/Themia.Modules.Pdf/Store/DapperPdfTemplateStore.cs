using global::Dapper;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Tenancy;

namespace Themia.Modules.Pdf.Store;

/// <summary>Dapper-backed <see cref="IPdfTemplateStore"/>. Reads go through <see cref="ITenantQueryFactory"/>'s
/// per-query global-fallback override (rather than the app-wide <c>DapperDataOptions.IncludeGlobalRecordsForTenants</c>
/// default) so a global default template always resolves for a tenant, regardless of that option. Writes go
/// through <see cref="IRepository{T,TKey}"/> + <see cref="IUnitOfWork"/>, whose <c>DapperUnitOfWork</c> stamps
/// <c>tenant_id</c> from the ambient tenant on insert and scopes update/delete to the ambient tenant.</summary>
internal sealed class DapperPdfTemplateStore(
    ITenantQueryFactory queries,
    ISqlCompiler compiler,
    IDapperConnectionContext connection,
    IRepository<PdfTemplate, Guid> repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext) : IPdfTemplateStore
{
    public async Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Id == Guid.Empty)
        {
            template.AssignId(Guid.NewGuid());
        }

        PdfTemplateOwnership.ApplyOnCreate(template, tenantContext.CurrentTenantId);

        await repository.AddAsync(template, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var owned = await FindOwnedAsync(template.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateNotFoundException(template.Key);
        owned.Body = template.Body;
        owned.Name = template.Name;
        owned.Description = template.Description;
        repository.Update(owned);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return owned;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var owned = await FindOwnedAsync(id, cancellationToken).ConfigureAwait(false);
        if (owned is null)
        {
            return; // idempotent; a row outside the caller's scope is a no-op, not an error
        }

        repository.Remove(owned);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Owned lookup: includeGlobalRecords:false => a tenant scope sees only its own rows, a system scope
    // only globals — so writes touch only rows the current scope owns.
    private Task<PdfTemplate?> FindOwnedAsync(Guid id, CancellationToken cancellationToken) =>
        QueryFirstAsync(queries.For<PdfTemplate>(includeGlobalRecords: false).Where("id", id).Limit(1), cancellationToken);

    public Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        QueryFirstAsync(queries.For<PdfTemplate>(includeGlobalRecords: true).Where("id", id).Limit(1), cancellationToken);

    public async Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await QueryListAsync(queries.For<PdfTemplate>(includeGlobalRecords: true), cancellationToken).ConfigureAwait(false);

    public async Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var candidates = await QueryListAsync(
            queries.For<PdfTemplate>(includeGlobalRecords: true).Where("key", key), cancellationToken).ConfigureAwait(false);
        return PdfTemplateResolver.PreferTenant(candidates) ?? throw new TemplateNotFoundException(key);
    }

    private async Task<IReadOnlyList<PdfTemplate>> QueryListAsync(SqlKata.Query query, CancellationToken cancellationToken)
    {
        var sql = compiler.Compile(query);
#pragma warning disable THEMIA103 // Deliberate bypass: `query` is always seeded by ITenantQueryFactory.For<T>()
        // above (tenant predicate + soft-delete filter already applied), so the raw connection here executes
        // an already tenant-scoped statement rather than an unscoped one. This module has no other sanctioned
        // way to execute a compiled SqlKata query, since ITenantQueryFactory only builds it.
        var conn = await connection.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore THEMIA103
        var rows = await conn.QueryAsync<PdfTemplate>(
                new CommandDefinition(sql.Sql, sql.Parameters, connection.CurrentTransaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<PdfTemplate?> QueryFirstAsync(SqlKata.Query query, CancellationToken cancellationToken)
    {
        var sql = compiler.Compile(query);
#pragma warning disable THEMIA103 // Deliberate bypass: see QueryListAsync above — `query` is always seeded by
        // ITenantQueryFactory.For<T>() before reaching this helper.
        var conn = await connection.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore THEMIA103
        return await conn.QueryFirstOrDefaultAsync<PdfTemplate>(
                new CommandDefinition(sql.Sql, sql.Parameters, connection.CurrentTransaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
