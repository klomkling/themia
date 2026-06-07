using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Themia.Quartz;

namespace Themia.Modules.Scheduling;

/// <summary>
/// EF Core-backed implementation of <see cref="IExecutionHistoryStore"/> that persists
/// Quartz job-execution history to a relational database via <see cref="SchedulingDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// This store is registered as a singleton. Each public operation creates a short-lived
/// <see cref="SchedulingDbContext"/> via <see cref="IDbContextFactory{TContext}"/> and disposes it
/// before returning, so concurrent Quartz listener callbacks (multiple worker threads) are safe —
/// no shared <c>DbContext</c> state exists between calls.
/// </para>
/// <para>
/// <b>Counter mechanism:</b> Executed/failed totals are stored in a separate
/// <see cref="SchedulerStatsRecord"/> row (keyed by <see cref="SchedulerName"/>).
/// Increment operations use a raw SQL <c>UPDATE … SET col = col + 1</c> statement so
/// that concurrent job completions never collide on the same EF tracked entity.
/// Gaps from aborted transactions are acceptable; duplicate increments are not.
/// </para>
/// <para>
/// <b>Purge behaviour:</b> <see cref="Purge"/> deletes history entries that are not in
/// the top-10 most recent per trigger (matching <see cref="InProcExecutionHistoryStore"/>
/// semantics). Call it periodically to bound table growth.
/// </para>
/// </remarks>
public sealed class EfExecutionHistoryStore : IExecutionHistoryStore
{
    private readonly IDbContextFactory<SchedulingDbContext> contextFactory;
    private readonly ILogger<EfExecutionHistoryStore> logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EfExecutionHistoryStore"/>.
    /// </summary>
    /// <param name="contextFactory">
    /// Factory used to create a short-lived <see cref="SchedulingDbContext"/> per operation.
    /// </param>
    /// <param name="logger">Logger for diagnostic messages.</param>
    public EfExecutionHistoryStore(IDbContextFactory<SchedulingDbContext> contextFactory, ILogger<EfExecutionHistoryStore> logger)
    {
        this.contextFactory = contextFactory;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string SchedulerName { get; set; } = string.Empty;

    /// <inheritdoc/>
    public async Task<ExecutionHistoryEntry?> Get(string fireInstanceId)
    {
        await using var context = contextFactory.CreateDbContext();

        var record = await context.ExecutionHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.FireInstanceId == fireInstanceId)
            .ConfigureAwait(false);

        return record is null ? null : ToEntry(record);
    }

    /// <inheritdoc/>
    public async Task Save(ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // FireInstanceId is the primary key; null/empty would insert an empty-string PK and collide.
        ArgumentException.ThrowIfNullOrEmpty(entry.FireInstanceId);

        await using var context = contextFactory.CreateDbContext();

        var existing = await context.ExecutionHistory
            .FirstOrDefaultAsync(r => r.FireInstanceId == entry.FireInstanceId)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.ExecutionHistory.Add(ToRecord(entry));
            try
            {
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Narrow race between the FirstOrDefault check and this insert (two threads saving the
                // same FireInstanceId). The row now exists; discard tracked state — already persisted.
                context.ChangeTracker.Clear();
            }
        }
        else
        {
            UpdateRecord(existing, entry);
            // Let UPDATE failures propagate: this path writes the job's finished-time/exception result,
            // and swallowing a genuine DbUpdateException here would silently lose it.
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task Purge()
    {
        await using var context = contextFactory.CreateDbContext();

        // Retain the top-10 most recent entries per trigger for this scheduler (matching InProcExecutionHistoryStore).
        // Compute the keep-set in memory: EF Core cannot translate per-group TAKE ("top-N per trigger") to SQL
        // portably — the same reason FilterLastOf* materialize first. Project only the columns needed so the
        // pull is light; it is bounded by 10×triggers after the first purge.
        var rows = await context.ExecutionHistory
            .AsNoTracking()
            .Where(r => r.SchedulerName == SchedulerName)
            .Select(r => new { r.FireInstanceId, r.Trigger, r.ActualFireTimeUtc })
            .ToListAsync()
            .ConfigureAwait(false);

        var keepIds = rows
            .GroupBy(r => r.Trigger)
            .SelectMany(g => g.OrderByDescending(r => r.ActualFireTimeUtc).Take(10).Select(r => r.FireInstanceId))
            .ToHashSet();

        var deleted = await context.ExecutionHistory
            .Where(r => r.SchedulerName == SchedulerName && !keepIds.Contains(r.FireInstanceId))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogDebug(
                "Purged {Count} execution history records for scheduler {SchedulerName}",
                deleted,
                SchedulerName);
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryJob(int limitPerJob)
    {
        await using var context = contextFactory.CreateDbContext();

        // Fetch all history for this scheduler then group in memory — EF Core cannot translate
        // per-group TAKE in a single SQL query portably across SQL Server / MySQL / PostgreSQL.
        var records = await context.ExecutionHistory
            .AsNoTracking()
            .Where(r => r.SchedulerName == SchedulerName)
            .OrderByDescending(r => r.ActualFireTimeUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        // Per group: most-recent N, then reversed to oldest→newest — matching InProcExecutionHistoryStore
        // so the dashboard histogram renders the time axis identically regardless of the backing store.
        return records
            .GroupBy(r => r.Job)
            .SelectMany(g => g.Take(limitPerJob).Reverse())
            .Select(ToEntry)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryTrigger(int limitPerTrigger)
    {
        await using var context = contextFactory.CreateDbContext();

        var records = await context.ExecutionHistory
            .AsNoTracking()
            .Where(r => r.SchedulerName == SchedulerName)
            .OrderByDescending(r => r.ActualFireTimeUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        // Per group: most-recent N, then reversed to oldest→newest — matching InProcExecutionHistoryStore.
        return records
            .GroupBy(r => r.Trigger)
            .SelectMany(g => g.Take(limitPerTrigger).Reverse())
            .Select(ToEntry)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ExecutionHistoryEntry>> FilterLast(int limit)
    {
        await using var context = contextFactory.CreateDbContext();

        var records = await context.ExecutionHistory
            .AsNoTracking()
            .Where(r => r.SchedulerName == SchedulerName)
            .OrderByDescending(r => r.ActualFireTimeUtc)
            .Take(limit)
            .ToListAsync()
            .ConfigureAwait(false);

        // Most-recent N, reversed to oldest→newest — matching InProcExecutionHistoryStore's contract.
        records.Reverse();
        return records.Select(ToEntry).ToList();
    }

    /// <inheritdoc/>
    public async Task<int> GetTotalJobsExecuted()
    {
        var stats = await GetOrDefaultStatsAsync().ConfigureAwait(false);
        return stats?.TotalJobsExecuted ?? 0;
    }

    /// <inheritdoc/>
    public async Task<int> GetTotalJobsFailed()
    {
        var stats = await GetOrDefaultStatsAsync().ConfigureAwait(false);
        return stats?.TotalJobsFailed ?? 0;
    }

    /// <inheritdoc/>
    public async Task IncrementTotalJobsExecuted()
    {
        await using var context = contextFactory.CreateDbContext();

        await EnsureStatsRowAsync(context, SchedulerName).ConfigureAwait(false);

        // Raw SQL increment avoids optimistic-concurrency conflicts under concurrent job completion.
        var rows = await context.Database
            .ExecuteSqlRawAsync(
                "UPDATE scheduling.scheduler_stats SET total_jobs_executed = total_jobs_executed + 1 WHERE scheduler_name = {0}",
                SchedulerName)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            logger.LogWarning(
                "Counter increment matched no scheduler_stats row for {SchedulerName}; increment lost.",
                SchedulerName);
        }
    }

    /// <inheritdoc/>
    public async Task IncrementTotalJobsFailed()
    {
        await using var context = contextFactory.CreateDbContext();

        await EnsureStatsRowAsync(context, SchedulerName).ConfigureAwait(false);

        var rows = await context.Database
            .ExecuteSqlRawAsync(
                "UPDATE scheduling.scheduler_stats SET total_jobs_failed = total_jobs_failed + 1 WHERE scheduler_name = {0}",
                SchedulerName)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            logger.LogWarning(
                "Counter increment matched no scheduler_stats row for {SchedulerName}; increment lost.",
                SchedulerName);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<SchedulerStatsRecord?> GetOrDefaultStatsAsync()
    {
        await using var context = contextFactory.CreateDbContext();

        return await context.SchedulerStats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SchedulerName == SchedulerName)
            .ConfigureAwait(false);
    }

    private static async Task EnsureStatsRowAsync(SchedulingDbContext context, string schedulerName)
    {
        var exists = await context.SchedulerStats
            .AnyAsync(s => s.SchedulerName == schedulerName)
            .ConfigureAwait(false);

        if (!exists)
        {
            context.SchedulerStats.Add(new SchedulerStatsRecord { SchedulerName = schedulerName });
            try
            {
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another concurrent request already inserted the row — ignore the duplicate.
                context.ChangeTracker.Clear();
            }
        }
    }

    private static ExecutionHistoryRecord ToRecord(ExecutionHistoryEntry entry) =>
        new()
        {
            FireInstanceId = entry.FireInstanceId ?? string.Empty,
            SchedulerInstanceId = entry.SchedulerInstanceId,
            SchedulerName = entry.SchedulerName,
            Job = entry.Job,
            Trigger = entry.Trigger,
            ScheduledFireTimeUtc = entry.ScheduledFireTimeUtc,
            ActualFireTimeUtc = entry.ActualFireTimeUtc,
            Recovering = entry.Recovering,
            Vetoed = entry.Vetoed,
            FinishedTimeUtc = entry.FinishedTimeUtc,
            ExceptionMessage = entry.ExceptionMessage,
        };

    private static void UpdateRecord(ExecutionHistoryRecord record, ExecutionHistoryEntry entry)
    {
        record.SchedulerInstanceId = entry.SchedulerInstanceId;
        record.SchedulerName = entry.SchedulerName;
        record.Job = entry.Job;
        record.Trigger = entry.Trigger;
        record.ScheduledFireTimeUtc = entry.ScheduledFireTimeUtc;
        record.ActualFireTimeUtc = entry.ActualFireTimeUtc;
        record.Recovering = entry.Recovering;
        record.Vetoed = entry.Vetoed;
        record.FinishedTimeUtc = entry.FinishedTimeUtc;
        record.ExceptionMessage = entry.ExceptionMessage;
    }

    private static ExecutionHistoryEntry ToEntry(ExecutionHistoryRecord record) =>
        new()
        {
            FireInstanceId = record.FireInstanceId,
            SchedulerInstanceId = record.SchedulerInstanceId,
            SchedulerName = record.SchedulerName,
            Job = record.Job,
            Trigger = record.Trigger,
            ScheduledFireTimeUtc = record.ScheduledFireTimeUtc,
            ActualFireTimeUtc = record.ActualFireTimeUtc,
            Recovering = record.Recovering,
            Vetoed = record.Vetoed,
            FinishedTimeUtc = record.FinishedTimeUtc,
            ExceptionMessage = record.ExceptionMessage,
        };
}
