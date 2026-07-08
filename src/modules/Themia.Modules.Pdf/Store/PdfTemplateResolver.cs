namespace Themia.Modules.Pdf.Store;

/// <summary>Shared resolution tiebreak so the EF and Dapper stores pick identically.</summary>
internal static class PdfTemplateResolver
{
    /// <summary>From a key-filtered candidate set (at most one tenant row and one global row),
    /// returns the tenant-owned row if present, else the global, else null.</summary>
    public static PdfTemplate? PreferTenant(IEnumerable<PdfTemplate> candidates)
    {
        PdfTemplate? tenantOwned = null;
        PdfTemplate? global = null;
        var tenantCount = 0;
        foreach (var c in candidates)
        {
            if (c.TenantId is not null)
            {
                tenantOwned ??= c;
                tenantCount++;
            }
            else
            {
                global ??= c;
            }
        }

        // Precondition: the per-tenant unique key index permits at most one tenant-owned row per key.
        // More than one signals data corruption — surface it loudly in debug builds without changing
        // release behavior (still returns the first tenant-owned row, else the global).
        System.Diagnostics.Debug.Assert(
            tenantCount <= 1, "PdfTemplateResolver.PreferTenant received more than one tenant-owned candidate for a key.");

        return tenantOwned ?? global;
    }
}
