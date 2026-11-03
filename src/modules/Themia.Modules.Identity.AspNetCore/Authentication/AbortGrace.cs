namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>A token that follows the caller's only <see cref="Duration"/> after the caller is cancelled.
/// A live request is unaffected: there is no timeout of its own. Used for work that records an outcome
/// that is already decided, so a client that aborts the request does not cost the record.</summary>
internal sealed class AbortGrace : IDisposable
{
    /// <summary>How long after the client aborts a decided outcome may keep running to be recorded.</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource grace;
    private readonly CancellationTokenRegistration link;

    public AbortGrace(CancellationToken caller, TimeProvider timeProvider)
    {
        grace = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        link = caller.Register(static state => ((CancellationTokenSource)state!).CancelAfter(Duration), grace);
    }

    public CancellationToken Token => grace.Token;

    public void Dispose()
    {
        link.Dispose();
        grace.Dispose();
    }
}
