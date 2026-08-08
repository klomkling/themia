using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Quartz;

namespace Themia.Scheduling;

/// <summary>
/// Persists Quartz job-execution history to the <c>scheduling</c> schema over plain ADO.NET, so
/// <c>/admin/jobs</c> keeps its history across a restart without an ORM.
/// </summary>
/// <remarks>
/// Behaviourally identical to the EF-backed store in <c>Themia.Modules.Scheduling</c> and to
/// <see cref="InProcExecutionHistoryStore"/> — including the ordering the dashboard depends on: the
/// filter methods return most-recent-N <b>reversed to oldest→newest</b>, so the histogram's time axis
/// renders the same whichever store is behind it. Getting that backwards produces a chart that looks
/// plausible and reads backwards.
/// <para>
/// Not registered by default. <see cref="InProcExecutionHistoryStore"/> remains what
/// <c>AddThemiaQuartz</c> registers, so adopting this package does not silently start writing rows to a
/// schema an existing host never asked for.
/// </para>
/// </remarks>
public sealed class DapperExecutionHistoryStore : IExecutionHistoryStore
{
    // Retained per trigger by Purge. Matches InProcExecutionHistoryStore and the EF store; changing it
    // here alone would make the dashboard's depth depend on which store is configured.
    private const int RetainedPerTrigger = 10;

    private readonly Func<DbConnection> connectionFactory;
    private readonly ILogger<DapperExecutionHistoryStore> logger;

    /// <summary>Creates the store.</summary>
    /// <param name="connectionFactory">Opens a connection to the database holding the <c>scheduling</c> schema.</param>
    /// <param name="logger">Logger.</param>
    public DapperExecutionHistoryStore(
        Func<DbConnection> connectionFactory,
        ILogger<DapperExecutionHistoryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        this.connectionFactory = connectionFactory;
        this.logger = logger;
    }

    /// <inheritdoc />
    public string SchedulerName { get; set; } = "QuartzScheduler";

    /// <inheritdoc />
    public async Task<ExecutionHistoryEntry?> Get(string fireInstanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(fireInstanceId);

        await using var connection = connectionFactory();
        return await connection.QueryFirstOrDefaultAsync<ExecutionHistoryEntry>(
            $"SELECT {SelectColumns} FROM scheduling.execution_history WHERE fire_instance_id = @FireInstanceId",
            new { FireInstanceId = fireInstanceId }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Save(ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // fire_instance_id is the primary key; an empty one would insert a blank-key row and then collide
        // with every other blank-key save.
        ArgumentException.ThrowIfNullOrEmpty(entry.FireInstanceId);

        await using var connection = connectionFactory();

        // UPDATE-then-INSERT rather than an engine-specific upsert: the same two statements work on
        // PostgreSQL and SQL Server, and the ordering matters — the update carries the job's result
        // (finished time, exception), which is the write that must not be lost.
        var updated = await connection.ExecuteAsync(UpdateSql, ToParameters(entry)).ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(InsertSql, ToParameters(entry)).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Two threads saved the same fire instance and the other won between the UPDATE and this
            // INSERT. The row exists and carries the same data, so there is nothing to repair. Any other
            // failure of this insert would surface on the next Save for the same id, which does happen —
            // the plugin saves once on fire and again on completion.
            logger.LogDebug(
                "Execution history insert for {FireInstanceId} lost a race; the row already exists.",
                entry.FireInstanceId);
        }
    }

    /// <inheritdoc />
    public async Task Purge()
    {
        await using var connection = connectionFactory();

        // The keep-set is computed here rather than in SQL for the same reason the EF store does it:
        // "top N per group" has no portable formulation across engines. Bounded by
        // RetainedPerTrigger × triggers after the first purge, so the pull stays small.
        var rows = (await connection.QueryAsync<(string FireInstanceId, string? Trigger, DateTimeOffset ActualFireTimeUtc)>(
            "SELECT fire_instance_id, \"trigger\", actual_fire_time_utc FROM scheduling.execution_history "
            + "WHERE scheduler_name = @SchedulerName",
            new { SchedulerName }).ConfigureAwait(false)).AsList();

        var keep = rows
            .GroupBy(r => r.Trigger)
            .SelectMany(g => g.OrderByDescending(r => r.ActualFireTimeUtc).Take(RetainedPerTrigger))
            .Select(r => r.FireInstanceId)
            .ToHashSet(StringComparer.Ordinal);

        var doomed = rows.Select(r => r.FireInstanceId).Where(id => !keep.Contains(id)).ToList();
        if (doomed.Count == 0)
        {
            return;
        }

        var deleted = await connection.ExecuteAsync(
            "DELETE FROM scheduling.execution_history WHERE scheduler_name = @SchedulerName "
            + "AND fire_instance_id = @FireInstanceId",
            doomed.Select(id => new { SchedulerName, FireInstanceId = id })).ConfigureAwait(false);

        logger.LogDebug(
            "Purged {Count} execution history records for scheduler {SchedulerName}", deleted, SchedulerName);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryJob(int limitPerJob) =>
        FilterPerGroup(limitPerJob, e => e.Job);

    /// <inheritdoc />
    public Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryTrigger(int limitPerTrigger) =>
        FilterPerGroup(limitPerTrigger, e => e.Trigger);

    /// <inheritdoc />
    public async Task<IEnumerable<ExecutionHistoryEntry>> FilterLast(int limit)
    {
        await using var connection = connectionFactory();

        var entries = (await connection.QueryAsync<ExecutionHistoryEntry>(
            $"SELECT {SelectColumns} FROM scheduling.execution_history WHERE scheduler_name = @SchedulerName "
            + "ORDER BY actual_fire_time_utc DESC",
            new { SchedulerName }).ConfigureAwait(false)).Take(limit).ToList();

        // Oldest→newest, matching the in-proc store's contract: the dashboard plots these left to right.
        entries.Reverse();
        return entries;
    }

    /// <inheritdoc />
    public Task<int> GetTotalJobsExecuted() => GetCounter("total_jobs_executed");

    /// <inheritdoc />
    public Task<int> GetTotalJobsFailed() => GetCounter("total_jobs_failed");

    /// <inheritdoc />
    public Task IncrementTotalJobsExecuted() => Increment("total_jobs_executed");

    /// <inheritdoc />
    public Task IncrementTotalJobsFailed() => Increment("total_jobs_failed");

    // "trigger" is quoted because TRIGGER is a reserved keyword on SQL Server — unquoted it is a syntax
    // error there while working fine on PostgreSQL, so the defect would appear on one engine only. ANSI
    // double quotes serve both: PostgreSQL always, and SQL Server because SqlClient sets QUOTED_IDENTIFIER
    // ON by default. The EF store never hit this because EF quotes every identifier for you.
    //
    // The ALIAS needs it too, and that is the half this first shipped without: "trigger" AS Trigger is
    // still "incorrect syntax near the keyword 'Trigger'" on SQL Server. Quoting the column and leaving
    // the alias bare fixed nothing and looked fixed — caught only because the suite runs both engines.
    private const string SelectColumns =
        "fire_instance_id AS FireInstanceId, scheduler_instance_id AS SchedulerInstanceId, "
        + "scheduler_name AS SchedulerName, job AS Job, \"trigger\" AS \"Trigger\", "
        + "scheduled_fire_time_utc AS ScheduledFireTimeUtc, actual_fire_time_utc AS ActualFireTimeUtc, "
        + "recovering AS Recovering, vetoed AS Vetoed, finished_time_utc AS FinishedTimeUtc, "
        + "exception_message AS ExceptionMessage";

    private const string UpdateSql =
        "UPDATE scheduling.execution_history SET scheduler_instance_id = @SchedulerInstanceId, "
        + "scheduler_name = @SchedulerName, job = @Job, \"trigger\" = @Trigger, "
        + "scheduled_fire_time_utc = @ScheduledFireTimeUtc, actual_fire_time_utc = @ActualFireTimeUtc, "
        + "recovering = @Recovering, vetoed = @Vetoed, finished_time_utc = @FinishedTimeUtc, "
        + "exception_message = @ExceptionMessage WHERE fire_instance_id = @FireInstanceId";

    private const string InsertSql =
        "INSERT INTO scheduling.execution_history (fire_instance_id, scheduler_instance_id, scheduler_name, "
        + "job, \"trigger\", scheduled_fire_time_utc, actual_fire_time_utc, recovering, vetoed, "
        + "finished_time_utc, exception_message) VALUES (@FireInstanceId, @SchedulerInstanceId, "
        + "@SchedulerName, @Job, @Trigger, @ScheduledFireTimeUtc, @ActualFireTimeUtc, @Recovering, "
        + "@Vetoed, @FinishedTimeUtc, @ExceptionMessage)";

    private static object ToParameters(ExecutionHistoryEntry e) => new
    {
        e.FireInstanceId,
        e.SchedulerInstanceId,
        e.SchedulerName,
        e.Job,
        e.Trigger,
        e.ScheduledFireTimeUtc,
        e.ActualFireTimeUtc,
        e.Recovering,
        e.Vetoed,
        e.FinishedTimeUtc,
        e.ExceptionMessage,
    };

    private async Task<IEnumerable<ExecutionHistoryEntry>> FilterPerGroup(
        int limitPerGroup, Func<ExecutionHistoryEntry, string?> group)
    {
        await using var connection = connectionFactory();

        var entries = (await connection.QueryAsync<ExecutionHistoryEntry>(
            $"SELECT {SelectColumns} FROM scheduling.execution_history WHERE scheduler_name = @SchedulerName "
            + "ORDER BY actual_fire_time_utc DESC",
            new { SchedulerName }).ConfigureAwait(false)).AsList();

        // Per group: most-recent N, then reversed to oldest→newest — the in-proc store's contract.
        return entries
            .GroupBy(group)
            .SelectMany(g => g.Take(limitPerGroup).Reverse())
            .ToList();
    }

    private async Task<int> GetCounter(string column)
    {
        await using var connection = connectionFactory();

        // Column name is a private constant, never caller input — the parameterised value is the
        // scheduler name.
        return await connection.ExecuteScalarAsync<int?>(
            $"SELECT {column} FROM scheduling.scheduler_stats WHERE scheduler_name = @SchedulerName",
            new { SchedulerName }).ConfigureAwait(false) ?? 0;
    }

    private async Task Increment(string column)
    {
        await using var connection = connectionFactory();

        var updated = await connection.ExecuteAsync(
            $"UPDATE scheduling.scheduler_stats SET {column} = {column} + 1 WHERE scheduler_name = @SchedulerName",
            new { SchedulerName }).ConfigureAwait(false);

        if (updated > 0)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO scheduling.scheduler_stats (scheduler_name, total_jobs_executed, total_jobs_failed) "
                + $"VALUES (@SchedulerName, CASE WHEN @Column = 'total_jobs_executed' THEN 1 ELSE 0 END, "
                + "CASE WHEN @Column = 'total_jobs_failed' THEN 1 ELSE 0 END)",
                new { SchedulerName, Column = column }).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Another thread created the row first. Re-apply the increment against it rather than
            // returning — dropping it here is how a counter silently under-reports under load, which is
            // indistinguishable from jobs not running.
            var retried = await connection.ExecuteAsync(
                $"UPDATE scheduling.scheduler_stats SET {column} = {column} + 1 WHERE scheduler_name = @SchedulerName",
                new { SchedulerName }).ConfigureAwait(false);

            if (retried == 0)
            {
                logger.LogWarning(
                    "Counter increment matched no scheduler_stats row for {SchedulerName}; increment lost.",
                    SchedulerName);
            }
        }
    }
}
