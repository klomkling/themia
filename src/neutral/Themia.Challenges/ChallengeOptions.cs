namespace Themia.Challenges;

/// <summary>
/// Per-purpose tuning: the secret's shape, how long it lives, how many wrong guesses it tolerates,
/// how many can be outstanding at once, and the two rate-limit windows. Every setter validates
/// eagerly — an invalid value throws from the assignment itself, inside the <c>configure</c> callback
/// passed to <see cref="ChallengeOptions.ConfigurePurpose"/>, rather than surfacing later from some
/// separate validation sweep. That keeps the failure at the call site that caused it.
/// </summary>
public sealed class PurposeOptions
{
    private ChallengeFormat _format = ChallengeFormat.Numeric(6);
    private TimeSpan _ttl = TimeSpan.FromMinutes(5);
    private int _maxAttempts = 5;
    private int _maxLiveChallenges = 1;
    private (int Limit, TimeSpan Window) _perScopeWindow = (3, TimeSpan.FromMinutes(15));
    private (int Limit, TimeSpan Window) _perKeyWindow = (10, TimeSpan.FromHours(1));

    /// <summary>The shape of the secret this purpose issues. Defaults to a 6-digit numeric code.</summary>
    /// <exception cref="ArgumentNullException">Assigned <see langword="null"/>.</exception>
    public ChallengeFormat Format
    {
        get => _format;
        set => _format = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>How long an issued secret remains valid. Defaults to 5 minutes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public TimeSpan Ttl
    {
        get => _ttl;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _ttl = value;
        }
    }

    /// <summary>
    /// How many incorrect verify attempts a single issued secret tolerates before it is exhausted.
    /// Defaults to 5. Not removable — only its value is tunable, because an unbounded number of
    /// guesses defeats the purpose of a short numeric code.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxAttempts = value;
        }
    }

    /// <summary>
    /// How many un-consumed, unexpired challenges may exist at once for a single scope. Defaults to 1:
    /// issuing a new secret supersedes any earlier one still outstanding for the same key and purpose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int MaxLiveChallenges
    {
        get => _maxLiveChallenges;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxLiveChallenges = value;
        }
    }

    /// <summary>
    /// The rate limit on issuance for one scope (key + purpose + tenant): at most <c>Limit</c> secrets
    /// may be issued within <c>Window</c>. Defaults to 3 per 15 minutes. Narrower than
    /// <see cref="PerKeyWindow"/>, which caps the same key across every purpose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerScopeWindow
    {
        get => _perScopeWindow;
        set => _perScopeWindow = ValidateWindow(value);
    }

    /// <summary>
    /// The rate limit on issuance for one key across all purposes: at most <c>Limit</c> secrets may be
    /// issued to the same key within <c>Window</c>, regardless of purpose. Defaults to 10 per hour.
    /// Wider than <see cref="PerScopeWindow"/> — it exists to cap an attacker cycling through purposes
    /// against the same phone number or email address.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerKeyWindow
    {
        get => _perKeyWindow;
        set => _perKeyWindow = ValidateWindow(value);
    }

    private static (int Limit, TimeSpan Window) ValidateWindow((int Limit, TimeSpan Window) value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Window, TimeSpan.Zero);
        return value;
    }
}

/// <summary>
/// Per-purpose configuration for <c>Themia.Challenges</c>. A purpose (e.g. <c>"login"</c>,
/// <c>"password-reset"</c>) must be configured with <see cref="ConfigurePurpose"/> before any
/// challenge can be issued or verified against it — there is no implicit default purpose.
/// </summary>
public sealed class ChallengeOptions
{
    private readonly Dictionary<string, PurposeOptions> _purposes = new(StringComparer.Ordinal);

    /// <summary>
    /// Configures (or reconfigures) a purpose. Validation is eager: an invalid value throws from the
    /// property setter inside <paramref name="configure"/>, not from a later validation pass.
    /// </summary>
    /// <param name="purpose">The purpose name, e.g. <c>"login"</c> or <c>"password-reset"</c>.</param>
    /// <param name="configure">Callback that sets the purpose's tunables.</param>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public void ConfigurePurpose(string purpose, Action<PurposeOptions> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(configure);

        var options = _purposes.TryGetValue(purpose, out var existing) ? existing : new PurposeOptions();
        configure(options);
        _purposes[purpose] = options;
    }

    /// <summary>Retrieves the configuration for a purpose.</summary>
    /// <param name="purpose">The purpose name.</param>
    /// <returns>The purpose's configuration.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="purpose"/> was never configured via <see cref="ConfigurePurpose"/>.
    /// </exception>
    public PurposeOptions GetPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        if (_purposes.TryGetValue(purpose, out var options))
        {
            return options;
        }

        throw new InvalidOperationException(
            $"Purpose '{purpose}' was never configured. Call {nameof(ChallengeOptions)}.{nameof(ConfigurePurpose)}(\"{purpose}\", ...) before issuing or verifying against it.");
    }
}
