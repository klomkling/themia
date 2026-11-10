namespace Themia.Exceptional.Retention;

internal interface IRetentionPurger
{
    /// <summary>Deletes up to <paramref name="batchSize"/> unprotected rows created before <paramref name="olderThanUtc"/>.</summary>
    Task<int> PurgeBatchAsync(DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken);
}
