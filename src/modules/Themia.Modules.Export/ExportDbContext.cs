using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.EntityConfiguration;

namespace Themia.Modules.Export;

/// <summary>EF context for the export module's own tenant-scoped tables.</summary>
public sealed class ExportDbContext : ThemiaDbContext
{
    /// <summary>Creates the context. Tenant is resolved from the ambient accessor (set per run by the job).</summary>
    public ExportDbContext(
        DbContextOptions<ExportDbContext> options,
        ITenantContext? tenantContext = null,
        IDataFilterScope? filterScope = null)
        : base(options, tenantContext)
    {
        _ = filterScope; // referenced for DI symmetry; query filters read DataFilterScope's ambient flag.
    }

    /// <summary>Export run records.</summary>
    public DbSet<ExportRun> Runs => Set<ExportRun>();

    /// <summary>Export schedules.</summary>
    public DbSet<ExportSchedule> Schedules => Set<ExportSchedule>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ExportRunConfiguration());
        modelBuilder.ApplyConfiguration(new ExportScheduleConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
