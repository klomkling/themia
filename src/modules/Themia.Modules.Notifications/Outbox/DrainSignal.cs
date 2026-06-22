using System.Threading.Channels;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// In-process wake for the drainer, kicked after an enqueuing transaction commits. Coalescing:
/// repeated signals before the next drain collapse to a single wake. In-process only — in a
/// multi-instance deployment, cross-instance latency is bounded by the poll interval.
/// </summary>
public sealed class DrainSignal
{
    private readonly Channel<bool> channel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Wakes the drainer (non-blocking; coalesces with any pending signal).</summary>
    public void Signal() => channel.Writer.TryWrite(true);

    /// <summary>Completes when a signal is available or the token cancels.</summary>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once a signal has been read.</returns>
    public async Task WaitAsync(CancellationToken ct) => await channel.Reader.ReadAsync(ct);
}
