using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Pdf.Store;

/// <summary>Shared create-time ownership rule so the EF and Dapper stores stamp/validate tenancy
/// identically. A tenant scope owns its own rows; a no-tenant (system) scope may create only global
/// (null-tenant) templates.</summary>
internal static class PdfTemplateOwnership
{
    /// <summary>Applies the create-time ownership rule to <paramref name="template"/> for the given
    /// ambient tenant. Under a tenant scope a null TenantId is stamped to the ambient tenant and a
    /// different explicit tenant is rejected; under a no-tenant scope any non-null TenantId is rejected
    /// (only global templates may be created).</summary>
    public static void ApplyOnCreate(PdfTemplate template, TenantId? ambient)
    {
        if (ambient is { } current)
        {
            if (template.TenantId is null)
            {
                template.TenantId = current;
            }
            else if (template.TenantId != current)
            {
                throw new InvalidOperationException(
                    $"Cannot create a template owned by tenant '{template.TenantId}' from the '{current}' scope.");
            }
        }
        else if (template.TenantId is not null)
        {
            throw new InvalidOperationException(
                "A no-tenant (system) scope can only create global templates; TenantId must be null.");
        }
    }
}
