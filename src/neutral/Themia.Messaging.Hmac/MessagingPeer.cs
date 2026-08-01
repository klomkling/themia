namespace Themia.Messaging.Hmac;

/// <summary>A configured messaging peer: its header prefix, signing keys, clock tolerance and routes.</summary>
public sealed class MessagingPeer
{
    // Kept out of any public property: a single logger.LogInformation("{@Peer}", peer), a diagnostics
    // endpoint, or an adopter serialising this object must not be able to leak it. Themia.Messaging.Http
    // never sees this value — it asks SignOutbound to produce a signature instead.
    private readonly string outboundSecret;

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
        this.outboundSecret = outboundSecret;
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

    /// <summary>
    /// The inbound keys this side accepts, keyed by key id — supports rotation without a shared cutover.
    /// Internal: only <see cref="HmacVerifier"/>, in the same assembly, needs to read the raw secrets to
    /// verify a signature.
    /// </summary>
    internal IReadOnlyDictionary<string, string> InboundKeys { get; }

    /// <summary>Outbound route path templates keyed by message type name.</summary>
    public IReadOnlyDictionary<string, string> Routes { get; }

    /// <summary>The wire header names derived from <see cref="HeaderPrefix"/>.</summary>
    public HmacHeaderNames HeaderNames { get; }

    /// <summary>
    /// Signs <paramref name="canonical"/> with this peer's outbound key and returns the key id and
    /// signature together, so a caller (e.g. <c>Themia.Messaging.Http</c>'s dispatcher) never needs to
    /// hold, log, or otherwise handle the outbound secret itself.
    /// </summary>
    /// <param name="canonical">The canonical string from <see cref="ThemiaHmacV1.Canonicalize"/>.</param>
    /// <returns>The outbound key id and the lowercase-hex signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="canonical"/> is <see langword="null"/>.</exception>
    public (string KeyId, string Signature) SignOutbound(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return (OutboundKeyId, ThemiaHmacV1.Sign(canonical, outboundSecret));
    }
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
    /// <exception cref="InvalidOperationException">
    /// <paramref name="keyId"/> was already registered — last-write-wins would silently discard the
    /// first secret, the same class of configuration mistake <see cref="HmacOptions.AddPeer"/> already
    /// refuses to allow for duplicate peer names.
    /// </exception>
    public void Accept(string keyId, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (!_inboundKeys.TryAdd(keyId, secret))
        {
            throw new InvalidOperationException($"An inbound key id '{keyId}' is already registered.");
        }
    }

    /// <summary>Registers an outbound route path template for a message type.</summary>
    /// <param name="type">The message type name.</param>
    /// <param name="path">The route path template.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="type"/> was already routed — last-write-wins would silently discard the first
    /// path, the same class of configuration mistake <see cref="HmacOptions.AddPeer"/> already refuses
    /// to allow for duplicate peer names.
    /// </exception>
    public void Route(string type, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (_routes.ContainsKey(type))
        {
            throw new InvalidOperationException($"A route for message type '{type}' is already registered.");
        }

        _routes[type] = path;
    }

    /// <summary>Validates the configured values and builds the immutable <see cref="MessagingPeer"/>.</summary>
    /// <param name="name">The peer's name.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> is blank, no outbound key was set, no inbound key was accepted,
    /// <see cref="ClockSkewTolerance"/> is not positive, or a route was configured with no
    /// <see cref="BaseAddress"/> set.
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

        // A route with nowhere to send it is a contradiction, not a valid inbound-only configuration:
        // it would build cleanly and then dead-letter every message at dispatch time, looking like a
        // transport failure instead of the configuration mistake it is.
        if (_routes.Count > 0 && BaseAddress is null)
        {
            throw new InvalidOperationException(
                $"Peer '{name}' has {_routes.Count} route(s) configured but no BaseAddress: outbound dispatch "
                + "would have nowhere to send. Set BaseAddress, or remove the Route(...) calls if this peer is inbound-only.");
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
