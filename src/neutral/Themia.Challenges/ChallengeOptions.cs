namespace Themia.Challenges;

/// <summary>
/// Per-purpose tuning: the secret's shape, how long it lives, how many wrong guesses it tolerates,
/// how many can be outstanding at once, and the per-scope rate-limit window. Every setter validates
/// eagerly — an invalid value throws from the assignment itself, inside the <c>configure</c> callback
/// passed to <see cref="ChallengeOptions.ConfigurePurpose"/>, rather than surfacing later from some
/// separate validation sweep. That keeps the failure at the call site that caused it.
/// <para>
/// The per-key ceiling is deliberately <em>not</em> here: it spans every purpose, so it lives on
/// <see cref="ChallengeOptions.PerKeyWindow"/>. See that property for why.
/// </para>
/// </summary>
public sealed class PurposeOptions
{
    private ChallengeFormat _format = ChallengeFormat.Numeric(6);
    private TimeSpan _ttl = TimeSpan.FromMinutes(5);
    private int _maxAttempts = 5;
    private int _maxLiveChallenges = 1;
    private (int Limit, TimeSpan Window) _perScopeWindow = (3, TimeSpan.FromMinutes(15));

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
    /// <see cref="ChallengeOptions.PerKeyWindow"/>, which caps the same key across every purpose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerScopeWindow
    {
        get => _perScopeWindow;
        set => _perScopeWindow = ValidateWindow(value);
    }

    internal static (int Limit, TimeSpan Window) ValidateWindow((int Limit, TimeSpan Window) value)
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
    private int _challengeRetentionHours = 24;
    private (int Limit, TimeSpan Window) _perKeyWindow = (20, TimeSpan.FromHours(1));
    private (int Limit, TimeSpan Window)? _perKeyGlobalWindow;

    /// <summary>
    /// The rate limit on issuance for one key across all purposes: at most <c>Limit</c> secrets may be
    /// issued to the same key within <c>Window</c>, regardless of purpose. Defaults to 20 per hour.
    /// Wider than <see cref="PurposeOptions.PerScopeWindow"/> — it exists to cap an attacker cycling
    /// through purposes against the same phone number or email address.
    /// <para>
    /// This lives on the store, not on <see cref="PurposeOptions"/>, because it is a ceiling
    /// <em>across</em> purposes and therefore cannot have a per-purpose window. Counters are bucketed by
    /// window start, so a per-purpose window would floor the same key's counter to a different bucket
    /// per purpose: an attacker cycling through purposes configured with different window durations
    /// would get a fresh ceiling for each, which is exactly the attack this limit exists to stop. One
    /// window for the whole store makes that unrepresentable rather than merely discouraged.
    /// </para>
    /// <para>
    /// This is a cost ceiling, not a brute-force defense — <see cref="PurposeOptions.MaxAttempts"/>
    /// already stops brute force on a single issued secret. Keep this limit far above what a real user
    /// ever reaches: a real user asks once or twice, so even 10 already gives an attacker who merely
    /// knows the victim's phone number or email a cheap way to burn the ceiling and lock that person
    /// out of issuance until the window elapses. Widening it (currently 20) costs nothing against that
    /// attack — the attempt cap is what actually protects the secret — but a low value converts "an
    /// attacker knows your phone number" into "you can't receive an OTP for an hour". Do not lower
    /// this "to be safe"; it does not make brute force harder and it does make lockout easier.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerKeyWindow
    {
        get => _perKeyWindow;
        set => _perKeyWindow = PurposeOptions.ValidateWindow(value);
    }

    /// <summary>
    /// The purpose value reserved for the <see cref="PerKeyGlobalWindow"/> counter row. Rejected by
    /// <see cref="ConfigurePurpose"/> so an adopter cannot register a real purpose that shares the
    /// bucket. Exposed only so the reservation is discoverable — nothing else should reference it.
    /// </summary>
    /// <remarks>
    /// A sentinel is needed because the tenant-agnostic bucket is stored with <c>tenant_id IS NULL</c>,
    /// and that coordinate is already taken: a platform-level challenge (<see cref="ChallengeScope.TenantId"/>
    /// is <see langword="null"/>) writes its ordinary per-key row at exactly <c>(NULL, key, NULL)</c>. The
    /// two counters mean different things and must not share a row, so the global one carries this
    /// purpose rather than <see langword="null"/>.
    /// </remarks>
    public const string GlobalKeyBucketPurpose = "__themia_global_key__";

    /// <summary>
    /// An optional third rate-limit layer: at most <c>Limit</c> secrets to the same key within
    /// <c>Window</c> <b>across every tenant</b>. <see langword="null"/> by default, which disables it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PerKeyWindow"/> is bucketed by <c>(tenant_id, key)</c>, because two tenants may hold the
    /// same phone number and one tenant exhausting its ceiling must not lock the other out — that
    /// isolation is deliberate and stays. But the SMS invoice and the victim's inbox are <em>not</em>
    /// partitioned by tenant, so where an attacker can pick or create the tenant — a caller-influenced
    /// subdomain, header, or path segment, and especially self-serve tenant signup — the same real phone
    /// number can be charged <see cref="PerKeyWindow"/>'s limit once per tenant. This layer is the ceiling
    /// on the physical thing: one bucket per key, no tenant in it.
    /// </para>
    /// <para>
    /// Left off by default because it is wrong for the common deployment. When tenants come from
    /// configuration rather than from request input, a global bucket only adds a way for one tenant's
    /// traffic to refuse another's. Turn it on when tenant identity is attacker-influenced; size it above
    /// the busiest legitimate tenant's real usage for a single key, not at it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value whose <c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window)? PerKeyGlobalWindow
    {
        get => _perKeyGlobalWindow;
        set => _perKeyGlobalWindow = value is null ? null : PurposeOptions.ValidateWindow(value.Value);
    }

    /// <summary>
    /// Whether the background retention purge (<see cref="Internal.ChallengePurgeService"/>) runs at
    /// all. Defaults to <see langword="true"/> — this schema is new, so there is no pre-existing history
    /// enabling it could destroy, unlike an outbox where flipping this on for an existing deployment
    /// would delete accumulated rows on the first run.
    /// </summary>
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>
    /// How long a challenge row survives after it is no longer live (consumed or expired) before the
    /// purge hard-deletes it. Defaults to 24 hours. Applies only to the <c>challenges</c> table —
    /// <c>challenge_rate_windows</c> rows are purged on their own elapsed-window rule regardless of this
    /// setting: a window must outlive the challenges it counted, or purging it early hands an attacker a
    /// free reset of the per-key ceiling that bounds the SMS bill. See
    /// <see cref="IChallengeDialect.PurgeExpiredSql"/> and <see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int ChallengeRetentionHours
    {
        get => _challengeRetentionHours;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _challengeRetentionHours = value;
        }
    }

    /// <summary>
    /// The widest rate-limit window in play — the longest of <see cref="PerKeyWindow"/> and every
    /// registered purpose's <see cref="PurposeOptions.PerScopeWindow"/>
    /// duration. <see cref="Internal.ChallengePurgeService"/> uses this to compute how long a
    /// <c>challenge_rate_windows</c> row must survive: a fixed retention shorter than the widest
    /// configured window would purge a counter a still-active window depends on, silently resetting the
    /// cost ceiling the two-table split exists to protect (see <see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>).
    /// Returns <see cref="TimeSpan.Zero"/> if no purpose has been configured yet, which the caller treats
    /// as "nothing to purge" rather than "purge everything".
    /// </summary>
    internal TimeSpan WidestConfiguredWindow()
    {
        if (_purposes.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var widest = PerKeyWindow.Window;
        if (PerKeyGlobalWindow is { } global && global.Window > widest)
        {
            widest = global.Window;
        }

        foreach (var purpose in _purposes.Values)
        {
            if (purpose.PerScopeWindow.Window > widest)
            {
                widest = purpose.PerScopeWindow.Window;
            }
        }

        return widest;
    }

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

        if (string.Equals(purpose, GlobalKeyBucketPurpose, StringComparison.Ordinal))
        {
            // Registering it would make ordinary issuance write into the tenant-agnostic ceiling's own
            // counter row, so that ceiling would trip on unrelated traffic and its refunds would credit
            // the wrong bucket. Rejected here rather than documented, since nothing detects it later.
            throw new ArgumentException(
                $"'{GlobalKeyBucketPurpose}' is reserved for the {nameof(PerKeyGlobalWindow)} counter row and cannot be configured as a purpose.",
                nameof(purpose));
        }

        var options = _purposes.TryGetValue(purpose, out var existing) ? existing : new PurposeOptions();
        configure(options);
        _purposes[purpose] = options;
    }

    /// <summary>Retrieves the configuration for a purpose.</summary>
    /// <param name="purpose">The purpose name.</param>
    /// <returns>The purpose's configuration.</returns>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is null or empty.</exception>
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

    /// <summary>
    /// Validates the options. A no-op today: every tunable, on this type and on
    /// <see cref="PurposeOptions"/>, already validates eagerly from its own property setter (see the
    /// type's remarks), so there is nothing left to check at the top level. Called from
    /// <c>AddThemiaChallenges</c> regardless, matching every other Themia options type's registration
    /// flow, and as the seam a future package-level invariant (e.g. "at least one purpose configured")
    /// would hang off.
    /// </summary>
    public void Validate()
    {
    }
}
