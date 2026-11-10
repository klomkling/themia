using System.Data.Common;

namespace Themia.Exceptional.Alerts;

/// <summary>
/// Per-database SQL for the alert job. Separate from <see cref="IExceptionalSqlDialect"/> so existing custom
/// dialects keep compiling; alerts need a provider that implements this too. All time arithmetic uses the
/// database's own UTC clock (never the application's), so a skewed host or a non-UTC session time zone
/// cannot shift a lease or the hourly cap.
/// </summary>
/// <remarks>
/// Status values stored in <c>ExceptionAlertState.Status</c>: 1 = Claimed, 2 = Sent, 3 = Failed.
/// Only integers, strings and Guids are bound as parameters (no temporal parameters). The cleanup param is
/// @RetentionSeconds.
/// </remarks>
public interface IExceptionAlertSqlDialect
{
    /// <summary>Creates a new, unopened connection to the exception store.</summary>
    DbConnection CreateConnection();

    /// <summary>
    /// True when <paramref name="exception"/> is the unique-key violation raised by <see cref="ClaimSql"/>
    /// (another instance already holds the claim).
    /// </summary>
    bool IsDuplicateClaim(Exception exception);

    /// <summary>
    /// Alertable rows not yet claimed, one result row per (row, trigger), ordered by trigger then recency.
    /// Params: @ApplicationName, @LookbackSeconds, @QuietSeconds, @Threshold, @Limit. A row that is both New and
    /// Threshold appears under both triggers; the caller keeps the lowest.
    /// Columns: ExceptionId, Guid, ApplicationName, ErrorHash, AlertTrigger, Type, Message, DuplicateCount, FirstSeenUtc, LastSeenUtc.
    /// </summary>
    string CandidatesSql { get; }

    /// <summary>
    /// Inserts one Claimed state row; the unique (ExceptionId, AlertTrigger) key makes a second claim lose: either
    /// 0 rows affected, or an exception for which <see cref="IsDuplicateClaim"/> is true.
    /// Params: @ExceptionId, @AlertTrigger, @BatchId, @ApplicationName, @ErrorHash.
    /// </summary>
    string ClaimSql { get; }

    /// <summary>Rows currently claimed under @BatchId, with the candidate columns. Params: @ApplicationName, @BatchId.</summary>
    string ClaimedRowsSql { get; }

    /// <summary>
    /// The oldest BatchId whose claim is older than the lease and still has attempts left, or no row.
    /// Params: @ApplicationName, @LeaseSeconds, @MaxAttempts.
    /// </summary>
    string StaleBatchSql { get; }

    /// <summary>
    /// Re-claims a stale batch under a new BatchId (refreshes ClaimedAt, increments Attempts); only one
    /// instance can win. Params: @ApplicationName, @OldBatchId, @NewBatchId, @LeaseSeconds. Returns rows affected.
    /// </summary>
    string ReclaimSql { get; }

    /// <summary>
    /// Marks stale Claimed rows that used up their attempts as Failed. Params: @ApplicationName, @LeaseSeconds, @MaxAttempts.
    /// Returns rows affected.
    /// </summary>
    string FailExhaustedSql { get; }

    /// <summary>Marks the batch Sent, fenced on BatchId and Status = Claimed. Params: @BatchId. Returns rows affected.</summary>
    string MarkSentSql { get; }

    /// <summary>Marks the batch Failed, fenced on BatchId and Status = Claimed. Params: @BatchId. Returns rows affected.</summary>
    string MarkFailedSql { get; }

    /// <summary>Number of distinct batches sent in the last hour. No params.</summary>
    string SentBatchesLastHourSql { get; }

    /// <summary>Deletes Sent/Failed state rows older than @RetentionSeconds. Params: @ApplicationName, @RetentionSeconds. Returns rows affected.</summary>
    string CleanupSql { get; }
}
