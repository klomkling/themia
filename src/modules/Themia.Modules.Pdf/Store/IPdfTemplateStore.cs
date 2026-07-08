namespace Themia.Modules.Pdf.Store;

/// <summary>Tenant-aware CRUD + key resolution for <see cref="PdfTemplate"/>.</summary>
public interface IPdfTemplateStore
{
    /// <summary>Persists a new template. Under a tenant scope the row is tenant-owned; a global
    /// (null-tenant) template can only be created from a no-tenant (system) scope.</summary>
    Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing template within the current scope.</summary>
    Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the template with <paramref name="id"/> within the current scope.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Gets a template by id within the current scope (tenant-owned or global), or null.</summary>
    Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists templates visible to the current scope (the tenant's own plus global defaults).</summary>
    Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a template by key: the tenant's own row if present, else the global default,
    /// else throws <see cref="TemplateNotFoundException"/>.</summary>
    Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default);
}
