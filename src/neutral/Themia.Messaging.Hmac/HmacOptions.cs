namespace Themia.Messaging.Hmac;

/// <summary>Registry of configured <see cref="MessagingPeer"/>s, keyed by name.</summary>
public sealed class HmacOptions
{
    private readonly Dictionary<string, MessagingPeer> _peers = new(StringComparer.Ordinal);

    /// <summary>Configures, validates and registers a peer.</summary>
    /// <param name="name">The peer's unique name.</param>
    /// <param name="configure">Configures the peer's keys, header prefix and routes.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The configured peer failed validation; see <see cref="MessagingPeerBuilder"/>.</exception>
    public void AddPeer(string name, Action<MessagingPeerBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new MessagingPeerBuilder();
        configure(builder);
        _peers[name] = builder.Build(name);
    }

    /// <summary>Looks up a configured peer by name.</summary>
    /// <param name="name">The peer's name.</param>
    /// <param name="peer">The matched peer, when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a peer with <paramref name="name"/> was configured.</returns>
    public bool TryGetPeer(string name, out MessagingPeer? peer)
        => _peers.TryGetValue(name, out peer);
}
