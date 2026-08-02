namespace Themia.Messaging.AspNetCore;

/// <summary>
/// Configures the inbound HMAC verification filter: which peers are known to lack loop protection.
/// </summary>
public sealed class VerificationOptions
{
    private readonly Dictionary<string, bool> biDirectionalPeers = new(StringComparer.Ordinal);

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
