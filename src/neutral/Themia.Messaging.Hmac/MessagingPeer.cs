namespace Themia.Messaging.Hmac;

/// <summary>A configured messaging peer: its header prefix, signing keys, clock tolerance and routes.</summary>
public sealed class MessagingPeer
{
    internal MessagingPeer(
        string name,
        string headerPrefix,
        Uri? baseAddress,
        TimeSpan clockSkewTolerance,
        long maxBodyBytes,
        string outboundKeyId,
        string outboundSecret,
        IReadOnlyDictionary<string, string> inboundKeys,
        IReadOnlyDictionary<string, string> routes)
    {
        Name = name;
        HeaderPrefix = headerPrefix;
        BaseAddress = baseAddress;
        ClockSkewTolerance = clockSkewTolerance;
        MaxBodyBytes = maxBodyBytes;
        OutboundKeyId = outboundKeyId;
        OutboundSecret = outboundSecret;
        InboundKeys = inboundKeys;
        Routes = routes;
        HeaderNames = new HmacHeaderNames(headerPrefix);
    }

    /// <summary>The peer's name, used to look it up via <see cref="HmacOptions.TryGetPeer"/>.</summary>
    public string Name { get; }

    /// <summary>The header prefix this peer's requests use, e.g. <c>X-Propertiezy-</c>.</summary>
    public string HeaderPrefix { get; }

    /// <summary>The peer's base address for outbound requests; <see langword="null"/> for an inbound-only peer.</summary>
    public Uri? BaseAddress { get; }

    /// <summary>How far a timestamp may drift from now before <see cref="HmacVerdict.StaleTimestamp"/> applies.</summary>
    public TimeSpan ClockSkewTolerance { get; }

    /// <summary>The maximum accepted inbound request body size, in bytes.</summary>
    public long MaxBodyBytes { get; }

    /// <summary>The key id this side signs outbound requests with.</summary>
    public string OutboundKeyId { get; }

    /// <summary>The secret this side signs outbound requests with.</summary>
    public string OutboundSecret { get; }

    /// <summary>The inbound keys this side accepts, keyed by key id — supports rotation without a shared cutover.</summary>
    public IReadOnlyDictionary<string, string> InboundKeys { get; }

    /// <summary>Outbound route path templates keyed by message type name.</summary>
    public IReadOnlyDictionary<string, string> Routes { get; }

    /// <summary>The wire header names derived from <see cref="HeaderPrefix"/>.</summary>
    public HmacHeaderNames HeaderNames { get; }
}

/// <summary>Configures a <see cref="MessagingPeer"/> via <see cref="HmacOptions.AddPeer"/>.</summary>
public sealed class MessagingPeerBuilder
{
    private readonly Dictionary<string, string> _inboundKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);
    private string? _outboundKeyId;
    private string? _outboundSecret;

    /// <summary>The header prefix this peer's requests use. Defaults to <see cref="HmacHeaderNames.DefaultPrefix"/>.</summary>
    public string HeaderPrefix { get; set; } = HmacHeaderNames.DefaultPrefix;

    /// <summary>The peer's base address for outbound requests.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>How far a timestamp may drift from now before it is rejected as stale. Defaults to 5 minutes.</summary>
    public TimeSpan ClockSkewTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum accepted inbound request body size, in bytes. Defaults to 4 MB.</summary>
    public long MaxBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Sets the key id and secret this side signs outbound requests with.</summary>
    /// <param name="keyId">The outbound key id, sent in the key-id header.</param>
    /// <param name="secret">The outbound shared secret.</param>
    public void SignWith(string keyId, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        _outboundKeyId = keyId;
        _outboundSecret = secret;
    }

    /// <summary>Registers an inbound key this side accepts, keyed by key id. Call once per active or rotating key.</summary>
    /// <param name="keyId">The inbound key id.</param>
    /// <param name="secret">The inbound shared secret.</param>
    public void Accept(string keyId, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        _inboundKeys[keyId] = secret;
    }

    /// <summary>Registers an outbound route path template for a message type.</summary>
    /// <param name="type">The message type name.</param>
    /// <param name="path">The route path template.</param>
    public void Route(string type, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _routes[type] = path;
    }

    /// <summary>Validates the configured values and builds the immutable <see cref="MessagingPeer"/>.</summary>
    /// <param name="name">The peer's name.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> is blank, no outbound key was set, no inbound key was accepted, or
    /// <see cref="ClockSkewTolerance"/> is not positive.
    /// </exception>
    internal MessagingPeer Build(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A messaging peer name must not be blank.");
        }

        if (_outboundKeyId is null || _outboundSecret is null)
        {
            throw new InvalidOperationException($"Peer '{name}' must call SignWith(...) to set an outbound key.");
        }

        if (_inboundKeys.Count == 0)
        {
            throw new InvalidOperationException($"Peer '{name}' must call Accept(...) at least once to configure an inbound key.");
        }

        if (ClockSkewTolerance <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Peer '{name}' must have a positive ClockSkewTolerance.");
        }

        return new MessagingPeer(
            name,
            HeaderPrefix,
            BaseAddress,
            ClockSkewTolerance,
            MaxBodyBytes,
            _outboundKeyId,
            _outboundSecret,
            _inboundKeys,
            _routes);
    }
}
