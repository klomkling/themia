namespace Themia.Messaging.Outbox;

/// <summary>
/// Delivers one claimed outbox row. This is the seam that keeps the drainer payload-agnostic: the
/// drainer owns claiming, leasing, backoff and dead-lettering, and the dispatcher owns what delivery
/// means and — importantly — whether a failure is retryable.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape this dispatcher delivers.</typeparam>
public interface IOutboxDispatcher<in TRow>
    where TRow : IClaimedRow
{
    /// <summary>
    /// Attempts delivery and reports the outcome. Implementations should REPORT failures via
    /// <see cref="DispatchResult"/> rather than throwing, so the drainer can record them on the row;
    /// a thrown exception is treated as transient. <see cref="OperationCanceledException"/> is the one
    /// exception that must be allowed to propagate — it means host shutdown, not a delivery failure,
    /// and swallowing it would burn an attempt and dead-letter rows on every deploy.
    /// </summary>
    /// <param name="scopedServices">A per-batch service scope for resolving delivery dependencies.</param>
    /// <param name="row">The claimed row to deliver.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The outcome of the attempt.</returns>
    Task<DispatchResult> DispatchAsync(IServiceProvider scopedServices, TRow row, CancellationToken ct);
}
