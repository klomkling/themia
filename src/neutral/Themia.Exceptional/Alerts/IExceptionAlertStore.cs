namespace Themia.Exceptional.Alerts;

/// <summary>Persistence the alert job needs: candidate selection and the DB-held claim state.</summary>
internal interface IExceptionAlertStore
{
    Task<IReadOnlyList<ExceptionAlertCandidate>> GetCandidatesAsync(
        string applicationName, ExceptionAlertOptions options, int limit, CancellationToken cancellationToken);

    /// <summary>True when this caller won the claim for the candidate; false when another instance holds it.</summary>
    Task<bool> TryClaimAsync(ExceptionAlertCandidate candidate, Guid batchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExceptionAlertCandidate>> GetClaimedAsync(string applicationName, Guid batchId, CancellationToken cancellationToken);

    Task<Guid?> FindStaleBatchAsync(string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>True when this caller took over the stale batch; only one instance can.</summary>
    Task<bool> TryReclaimAsync(string applicationName, Guid oldBatchId, Guid newBatchId, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks stale batches that used up their attempts as failed; returns the number of rows.</summary>
    Task<int> FailExhaustedAsync(string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);

    Task<int> MarkSentAsync(Guid batchId, CancellationToken cancellationToken);

    Task<int> MarkFailedAsync(Guid batchId, CancellationToken cancellationToken);

    Task<int> CountSentBatchesLastHourAsync(string applicationName, CancellationToken cancellationToken);

    Task<int> CleanupAsync(string applicationName, TimeSpan retention, CancellationToken cancellationToken);
}
