using System.Threading.Channels;

namespace Themia.Messaging.Outbox;

/// <summary>
/// In-process wake for the drainer, kicked after an enqueuing transaction commits. Coalescing:
/// repeated signals before the next drain collapse to a single wake. In-process only — in a
/// multi-instance deployment, cross-instance latency is bounded by the poll interval.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape whose drainer this wakes, so multiple outboxes sharing one
/// host (e.g. Messaging and Notifications) each get their own signal instead of racing a shared one —
/// mirrors <see cref="OutboxDrainerOptions{TRow}"/> and <see cref="IOutboxPurgeDialect{TRow}"/>.</typeparam>
public sealed class DrainSignal<TRow>
    where TRow : IClaimedRow
{
    private readonly Channel<bool> channel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Wakes the drainer (non-blocking; coalesces with any pending signal).</summary>
    public void Signal() => channel.Writer.TryWrite(true);

    /// <summary>Completes when a signal is available or the token cancels.</summary>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once a signal has been read.</returns>
    public async Task WaitAsync(CancellationToken ct) => await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
}
