using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Pdf;

/// <summary>A stored HTML/Handlebars template resolved per tenant with a global-default fallback.</summary>
public sealed class PdfTemplate : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <summary>Owning tenant; <c>null</c> for a global default template.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>Resolution key (e.g. "invoice"). Unique per tenant, and once globally.</summary>
    public required string Key { get; set; }

    /// <summary>The Handlebars/HTML template source rendered against a model.</summary>
    public required string Body { get; set; }

    /// <summary>Human-readable label for management UIs.</summary>
    public string? Name { get; set; }

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Assigns the primary key. Used by the EF Core and Dapper stores when persisting a new template.</summary>
    /// <param name="id">The identifier to assign.</param>
    internal void AssignId(Guid id) => Id = id;
}
