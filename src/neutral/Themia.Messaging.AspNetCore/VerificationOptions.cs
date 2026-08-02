namespace Themia.Messaging.AspNetCore;

/// <summary>
/// Configures the inbound HMAC verification filter: whether the loop guard runs, and which peers are
/// known to lack loop protection.
/// </summary>
public sealed class VerificationOptions
{
    private readonly Dictionary<string, bool> biDirectionalPeers = new(StringComparer.Ordinal);

    /// <summary>
    /// Turns the loop guard off, so every verified request reaches the endpoint even when it carries this
    /// service's own origin. Defaults to <see langword="false"/> (the guard runs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost nothing should set this. The guard fires only when a message arrives carrying the origin of
    /// the service receiving it, which normally means it has come back to its own creator and reprocessing
    /// it would duplicate the work — on a bi-directional channel, unboundedly.
    /// </para>
    /// <para>
    /// The exception is an <b>echo topology</b>: a peer that replies by returning the inbound envelope with
    /// its <c>Origin</c> preserved, so the originator can correlate the reply. Those replies legitimately
    /// carry the receiver's own origin, and with the guard on they are dropped with a 200 — which the
    /// sender's dispatcher records as <c>Delivered</c>, so the reply is lost silently on both sides. A host
    /// built that way sets this to <see langword="true"/> and takes responsibility for its own loop
    /// termination.
    /// </para>
    /// </remarks>
    public bool DisableLoopGuard { get; set; }

    /// <summary>
    /// Declares <paramref name="peerName"/> as a channel used for bi-directional messaging (this service
    /// both sends to and receives from it), for the <c>AddThemiaMessagingVerification</c> startup warning.
    /// </summary>
    /// <remarks>
    /// The loop guard can only compare an <c>Origin</c> header the peer actually sends. Whether it does is
    /// external knowledge the framework cannot observe at startup — it depends on the peer's own
    /// implementation, e.g. a legacy link that emits only the two mandatory headers. Declaring
    /// <paramref name="sendsOriginHeader"/> <see langword="false"/> for such a peer makes that gap visible
    /// at boot: loop protection on that channel is absent, not merely degraded, and a cycling message
    /// would otherwise be accepted and re-processed rather than dropped.
    /// </remarks>
    /// <param name="peerName">The peer's name, as registered via <c>HmacOptions.AddPeer</c>.</param>
    /// <param name="sendsOriginHeader">
    /// Whether the peer's inbound requests carry an <c>Origin</c> header. Defaults to <see langword="true"/>
    /// (no warning); set <see langword="false"/> for a peer known not to send one.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="peerName"/> is null or empty.</exception>
    public void MarkBiDirectional(string peerName, bool sendsOriginHeader = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerName);
        biDirectionalPeers[peerName] = sendsOriginHeader;
    }

    /// <summary>Peers declared via <see cref="MarkBiDirectional"/>, keyed by name, with whether each sends an Origin header.</summary>
    internal IReadOnlyDictionary<string, bool> BiDirectionalPeers => biDirectionalPeers;
}
