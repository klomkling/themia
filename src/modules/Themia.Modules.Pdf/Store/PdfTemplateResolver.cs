namespace Themia.Modules.Pdf.Store;

/// <summary>Shared resolution tiebreak so the EF and Dapper stores pick identically.</summary>
internal static class PdfTemplateResolver
{
    /// <summary>From a key-filtered candidate set (at most one tenant row and one global row),
    /// returns the tenant-owned row if present, else the global, else null.</summary>
    public static PdfTemplate? PreferTenant(IEnumerable<PdfTemplate> candidates)
    {
        PdfTemplate? global = null;
        foreach (var c in candidates)
        {
            if (c.TenantId is not null)
            {
                return c; // tenant-owned wins outright
            }
            global ??= c;
        }
        return global;
    }
}
