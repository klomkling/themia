using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Pdf.EntityConfiguration;

namespace Themia.Modules.Pdf;

/// <summary>EF context for the PDF module's template table. Tenant, soft-delete and global-inclusion
/// filters come from <see cref="ThemiaDbContext"/>.</summary>
public sealed class PdfDbContext : ThemiaDbContext
{
    /// <summary>Creates the context. Tenant is resolved from the injected accessor.</summary>
    public PdfDbContext(DbContextOptions<PdfDbContext> options, ITenantContext? tenantContext = null)
        : base(options, tenantContext)
    {
    }

    /// <summary>The stored templates.</summary>
    public DbSet<PdfTemplate> Templates => Set<PdfTemplate>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PdfTemplateConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
