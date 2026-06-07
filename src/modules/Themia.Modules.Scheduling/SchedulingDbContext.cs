using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.EFCore;

namespace Themia.Modules.Scheduling;

/// <summary>
/// EF Core <see cref="DbContext"/> for the scheduling module, holding execution history
/// and per-scheduler counter records.
/// </summary>
/// <remarks>
/// Derives from <see cref="ThemiaDbContext"/> to inherit audit, soft-delete, and
/// naming-convention infrastructure. Tenant filters are NOT applied to the scheduling
/// entities because neither <see cref="ExecutionHistoryRecord"/> nor
/// <see cref="SchedulerStatsRecord"/> implements <c>ITenantEntity</c> — execution
/// history is global, process-wide scheduler infrastructure.
/// </remarks>
public sealed class SchedulingDbContext : ThemiaDbContext
{
    /// <summary>
    /// Initializes a new instance of <see cref="SchedulingDbContext"/>.
    /// </summary>
    /// <param name="options">EF Core context options.</param>
    public SchedulingDbContext(DbContextOptions<SchedulingDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gets the execution history records for all schedulers.</summary>
    public DbSet<ExecutionHistoryRecord> ExecutionHistory => Set<ExecutionHistoryRecord>();

    /// <summary>Gets the per-scheduler job-execution counters.</summary>
    public DbSet<SchedulerStatsRecord> SchedulerStats => Set<SchedulerStatsRecord>();

    /// <inheritdoc />
    protected override bool EnableTenantFilters => false;

    /// <inheritdoc />
    protected override bool EnableSoftDeleteFilters => false;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ExecutionHistoryRecord>(entity =>
        {
            entity.ToTable("execution_history", schema: "scheduling");
            entity.HasKey(e => e.FireInstanceId);
            entity.Property(e => e.FireInstanceId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SchedulerInstanceId).HasMaxLength(256);
            entity.Property(e => e.SchedulerName).HasMaxLength(256);
            entity.Property(e => e.Job).HasMaxLength(512);
            entity.Property(e => e.Trigger).HasMaxLength(512);
            entity.Property(e => e.ExceptionMessage).HasMaxLength(4000);

            // DateTimeOffset stored as timestamptz (Postgres) or datetimeoffset (SQL Server).
            // EF Core maps DateTimeOffset correctly for both providers — no custom converter needed.
            entity.Property(e => e.ScheduledFireTimeUtc);
            entity.Property(e => e.ActualFireTimeUtc).IsRequired();
            entity.Property(e => e.FinishedTimeUtc);

            // Composite index to accelerate FilterLastOfEveryTrigger / FilterLast queries,
            // which filter by SchedulerName and/or Trigger and order by ActualFireTimeUtc DESC.
            entity.HasIndex(e => new { e.SchedulerName, e.Trigger, e.ActualFireTimeUtc })
                  .HasDatabaseName("ix_execution_history_scheduler_trigger_fired");
        });

        modelBuilder.Entity<SchedulerStatsRecord>(entity =>
        {
            entity.ToTable("scheduler_stats", schema: "scheduling");
            entity.HasKey(e => e.SchedulerName);
            entity.Property(e => e.SchedulerName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.TotalJobsExecuted).IsRequired();
            entity.Property(e => e.TotalJobsFailed).IsRequired();
        });
    }
}
