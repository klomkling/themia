using System.Data.Common;
using Dapper;

namespace Themia.Exceptional.Alerts;

/// <summary>Dapper implementation of <see cref="IExceptionAlertStore"/> over an <see cref="IExceptionAlertSqlDialect"/>.</summary>
internal sealed class DapperExceptionAlertStore : IExceptionAlertStore
{
    private readonly IExceptionAlertSqlDialect dialect;

    public DapperExceptionAlertStore(IExceptionAlertSqlDialect dialect) => this.dialect = dialect;

    public async Task<IReadOnlyList<ExceptionAlertCandidate>> GetCandidatesAsync(
        string applicationName, ExceptionAlertOptions options, int limit, CancellationToken cancellationToken)
    {
        var args = new
        {
            ApplicationName = applicationName,
            LookbackSeconds = Seconds(options.Lookback),
            QuietSeconds = Seconds(options.QuietPeriod),
            Threshold = options.CountThreshold,
            Limit = limit,
        };
        return await QueryAsync(dialect.CandidatesSql, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryClaimAsync(ExceptionAlertCandidate candidate, Guid batchId, CancellationToken cancellationToken)
    {
        var args = new
        {
            candidate.ExceptionId,
            AlertTrigger = (int)candidate.Trigger,
            BatchId = batchId,
            candidate.ApplicationName,
            candidate.ErrorHash,
        };
        try
        {
            return await ExecuteAsync(dialect.ClaimSql, args, cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (DbException ex) when (dialect.IsDuplicateClaim(ex))
        {
            return false;
        }
    }

    public Task<IReadOnlyList<ExceptionAlertCandidate>> GetClaimedAsync(
        string applicationName, Guid batchId, CancellationToken cancellationToken) =>
        QueryAsync(dialect.ClaimedRowsSql, new { ApplicationName = applicationName, BatchId = batchId }, cancellationToken);

    public async Task<Guid?> FindStaleBatchAsync(string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            dialect.StaleBatchSql, new { ApplicationName = applicationName, LeaseSeconds = Seconds(lease), MaxAttempts = maxAttempts },
            cancellationToken: cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<Guid?>(command).ConfigureAwait(false);
    }

    public async Task<bool> TryReclaimAsync(
        string applicationName, Guid oldBatchId, Guid newBatchId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var args = new { ApplicationName = applicationName, OldBatchId = oldBatchId, NewBatchId = newBatchId, LeaseSeconds = Seconds(lease) };
        return await ExecuteAsync(dialect.ReclaimSql, args, cancellationToken).ConfigureAwait(false) > 0;
    }

    public Task<int> FailExhaustedAsync(
        string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
        ExecuteAsync(dialect.FailExhaustedSql, new { ApplicationName = applicationName, LeaseSeconds = Seconds(lease), MaxAttempts = maxAttempts }, cancellationToken);

    public Task<int> MarkSentAsync(Guid batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(dialect.MarkSentSql, new { BatchId = batchId }, cancellationToken);

    public Task<int> MarkFailedAsync(Guid batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(dialect.MarkFailedSql, new { BatchId = batchId }, cancellationToken);

    public async Task<int> CountSentBatchesLastHourAsync(string applicationName, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            dialect.SentBatchesLastHourSql, new { ApplicationName = applicationName }, cancellationToken: cancellationToken);
        return await connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public Task<int> CleanupAsync(string applicationName, TimeSpan retention, CancellationToken cancellationToken) =>
        ExecuteAsync(dialect.CleanupSql, new { ApplicationName = applicationName, RetentionSeconds = Seconds(retention) }, cancellationToken);

    private static int Seconds(TimeSpan span) => (int)Math.Min(span.TotalSeconds, int.MaxValue);

    private async Task<int> ExecuteAsync(string sql, object args, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ExceptionAlertCandidate>> QueryAsync(
        string sql, object args, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CandidateRow>(new CommandDefinition(sql, args, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.Select(r => r.ToCandidate()).ToList();
    }

    private sealed class CandidateRow
    {
        public long ExceptionId { get; set; }
        public Guid Guid { get; set; }
        public string ApplicationName { get; set; } = string.Empty;
        public string ErrorHash { get; set; } = string.Empty;
        public int AlertTrigger { get; set; }
        public string Type { get; set; } = string.Empty;
        public int DuplicateCount { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public ExceptionAlertCandidate ToCandidate() => new()
        {
            ExceptionId = ExceptionId,
            Guid = Guid,
            ApplicationName = ApplicationName,
            ErrorHash = ErrorHash,
            Trigger = (ExceptionAlertTrigger)AlertTrigger,
            Type = Type,
            DuplicateCount = DuplicateCount,
            FirstSeenUtc = DateTime.SpecifyKind(FirstSeenUtc, DateTimeKind.Utc),
            LastSeenUtc = DateTime.SpecifyKind(LastSeenUtc, DateTimeKind.Utc),
        };
    }
}
